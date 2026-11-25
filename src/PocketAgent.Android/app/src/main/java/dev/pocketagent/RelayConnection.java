package dev.pocketagent;

import android.util.Base64;
import java.io.IOException;
import java.nio.charset.StandardCharsets;
import java.security.SecureRandom;
import java.util.UUID;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;
import javax.crypto.Cipher;
import javax.crypto.spec.GCMParameterSpec;
import javax.crypto.spec.SecretKeySpec;
import okhttp3.OkHttpClient;
import okhttp3.Request;
import okhttp3.Response;
import okhttp3.WebSocket;
import okhttp3.WebSocketListener;
import org.json.JSONArray;
import org.json.JSONObject;

final class RelayConnection {
  private static final SecureRandom RANDOM = new SecureRandom();
  private static final int MAX_FRAME = 30 * 1024 * 1024;
  private static final OkHttpClient HTTP = new OkHttpClient.Builder()
      .followRedirects(false).followSslRedirects(false).connectTimeout(10, TimeUnit.SECONDS)
      .pingInterval(25, TimeUnit.SECONDS).build();
  private final RelayTarget target;
  private final SecretKeySpec key;
  private volatile Session session;
  private volatile boolean paused;
  private OkHttpClient socketClient;

  static final class Reply {
    final int status;
    final byte[] body;

    Reply(int status, byte[] body) {
      this.status = status;
      this.body = body;
    }
  }

  private static final class Session extends WebSocketListener {
    private final ArrayBlockingQueue<Object> events = new ArrayBlockingQueue<>(2);
    private volatile boolean ended;
    private volatile WebSocket socket;

    @Override
    public void onMessage(WebSocket socket, String text) {
      if (text.length() > MAX_FRAME || !events.offer(text))
        stop(new IOException("The relay response exceeded its size or queue limit."));
    }

    @Override
    public void onClosing(WebSocket socket, int code, String reason) {
      stop(new IOException("The relay connection closed."));
    }

    @Override
    public void onFailure(WebSocket socket, Throwable failure, Response response) {
      stop(new IOException("The relay connection failed.", failure));
    }

    void stop(IOException failure) {
      ended = true;
      events.clear();
      events.offer(failure);
      if (socket != null) socket.cancel();
    }

    JSONObject receive(int timeoutSeconds) throws Exception {
      Object event = events.poll(timeoutSeconds, TimeUnit.SECONDS);
      if (event instanceof IOException) throw (IOException) event;
      if (event == null) throw new IOException("The relay timed out.");
      return new JSONObject((String) event);
    }

    void send(JSONObject value) throws IOException {
      if (ended || !socket.send(value.toString())) throw new IOException("The relay connection closed.");
    }
  }

  RelayConnection(RelayTarget target) {
    this.target = target;
    String hex = target.key.substring(target.key.indexOf(':') + 1);
    byte[] bytes = new byte[32];
    for (int i = 0; i < bytes.length; i++) bytes[i] = (byte) Integer.parseInt(hex.substring(i * 2, i * 2 + 2), 16);
    key = new SecretKeySpec(bytes, "AES");
  }

  static final class ConnectionError extends IOException {
    ConnectionError(Exception cause) {
      super("Could not reach the desktop through the relay. Check that both desktops are online and the relay address and certificate are current.", cause);
    }
  }

  private Session connect(int timeoutSeconds) throws Exception {
    if (socketClient == null) {
      OkHttpClient.Builder builder = HTTP.newBuilder();
      if (!target.pin.isEmpty()) {
        PinnedTrust trust = new PinnedTrust(target.pin);
        builder.sslSocketFactory(trust.socketFactory(), trust);
      }
      socketClient = builder.build();
    }
    Session opened = new Session();
    session = opened;
    if (paused) throw new IOException("The saved connection is being checked.");
    opened.socket = socketClient.newWebSocket(new Request.Builder().url(target.endpoint).build(), opened);
    opened.send(new JSONObject().put("kind", "authenticate-client").put("hostId", target.host)
        .put("clientId", target.device).put("token", target.credential));
    if (!"authenticated".equals(opened.receive(Math.min(timeoutSeconds, 15)).optString("kind")))
      throw new IOException("Relay authentication failed. Check the enrollment and relay version.");
    return opened;
  }

  Reply request(String path, JSONObject body, String token, boolean image) throws Exception {
    return request(path, body, token, image, image ? 30 : 75);
  }

  synchronized Reply request(String path, JSONObject body, String token, boolean image, int timeoutSeconds) throws Exception {
    if (paused) throw new IOException("The saved connection is being checked.");
    try {
      Session active = session;
      if (active == null || active.ended) active = connect(timeoutSeconds);
      String id = UUID.randomUUID().toString();
      byte[] nonce = new byte[12];
      RANDOM.nextBytes(nonce);
      JSONArray headers = new JSONArray().put(header("Accept", image ? "image/*" : "application/json"));
      if (!token.isEmpty()) headers.put(header("Authorization", "Bearer " + token));
      if (body != null) headers.put(header("Content-Type", "application/json; charset=utf-8"));
      JSONObject request = new JSONObject().put("version", 1).put("method", body == null ? "GET" : "POST")
          .put("path", "/api" + path).put("headers", headers)
          .put("body", body == null ? "" : encode(body.toString().getBytes(StandardCharsets.UTF_8)));
      JSONObject sealed = new JSONObject().put("keyId", target.device).put("nonce", encode(nonce))
          .put("ciphertext", encode(cipher(Cipher.ENCRYPT_MODE, nonce, id).doFinal(request.toString().getBytes(StandardCharsets.UTF_8))));
      JSONObject route = new JSONObject().put("requestId", id).put("targetHost", target.host)
          .put("deviceId", target.device).put("envelope", sealed);
      active.send(new JSONObject().put("kind", "frame").put("frame",
          new JSONObject().put("version", 1).put("kind", "client-request").put("request", route)));
      JSONObject message = active.receive(timeoutSeconds);
      JSONObject frame = message.getJSONObject("frame");
      JSONObject response = frame.getJSONObject("response");
      if (!"frame".equals(message.optString("kind")) || frame.optInt("version") != 1
          || !"host-response".equals(frame.optString("kind")) || !id.equals(response.optString("requestId"))
          || !target.host.equals(response.optString("targetHost")))
        throw new IOException("The relay response did not match the request.");
      JSONObject envelope = response.getJSONObject("envelope");
      byte[] responseNonce = decode(envelope.getString("nonce"));
      if (responseNonce.length != 12 || !target.device.equals(envelope.optString("keyId")))
        throw new IOException("The relay response used an invalid key or nonce.");
      byte[] clear = cipher(Cipher.DECRYPT_MODE, responseNonce, id).doFinal(decode(envelope.getString("ciphertext")));
      JSONObject reply = new JSONObject(new String(clear, StandardCharsets.UTF_8));
      int status = reply.optInt("status");
      if (reply.optInt("version") != 1 || status < 100 || status > 599)
        throw new IOException("The relay response has an unsupported format.");
      byte[] bytes = decode(reply.getString("body"));
      if (bytes.length > (image ? 16 : 8) * 1024 * 1024)
        throw new ApiClient.ApiError(413, "The response is too large to load.");
      return new Reply(status, bytes);
    } catch (Exception failure) {
      // The request may already have reached the desktop. Reconnect on the next request.
      close();
      if (failure instanceof ApiClient.ApiError || failure instanceof InterruptedException) throw failure;
      throw new ConnectionError(failure);
    }
  }

  void close() {
    Session active = session;
    if (active != null) active.stop(new IOException("The relay connection closed."));
  }

  void pause(boolean value) {
    // The relay routes a device ID to one socket.
    paused = value;
    if (value) close();
  }

  private Cipher cipher(int mode, byte[] nonce, String requestId) throws Exception {
    Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
    cipher.init(mode, key, new GCMParameterSpec(128, nonce));
    cipher.updateAAD(("pocketagent/application/" + target.host + "/" + target.device + "/" + requestId)
        .getBytes(StandardCharsets.UTF_8));
    return cipher;
  }

  private static JSONObject header(String name, String value) throws Exception {
    return new JSONObject().put("key", name).put("value", value);
  }

  private static String encode(byte[] value) { return Base64.encodeToString(value, Base64.NO_WRAP); }
  private static byte[] decode(String value) { return Base64.decode(value, Base64.DEFAULT); }
}
