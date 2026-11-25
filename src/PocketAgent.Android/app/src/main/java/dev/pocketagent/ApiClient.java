package dev.pocketagent;

import java.io.ByteArrayOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URI;
import java.nio.charset.StandardCharsets;
import org.json.JSONObject;
import org.json.JSONTokener;

final class ApiClient {
  final String origin;
  final String stateKey;
  final String token;
  final String pin;
  final RelayTarget relay;
  private final RelayConnection connection;

  static final class ApiError extends Exception {
    final int status;

    ApiError(int status, String message) {
      super(message);
      this.status = status;
    }
  }

  ApiClient(String origin, String token) {
    this(origin, token, "");
  }

  ApiClient(String origin, String token, String pin) {
    this(origin, token, pin, null);
  }

  ApiClient(String origin, String token, String pin, RelayTarget relay) {
    this(origin, token, pin, relay, origin);
  }

  private ApiClient(String origin, String token, String pin, RelayTarget relay, String stateKey) {
    this.origin = origin;
    this.stateKey = stateKey;
    this.token = token;
    this.pin = pin;
    this.relay = relay;
    connection = relay == null ? null : new RelayConnection(relay);
  }

  static ApiClient fromSaved(JSONObject session) throws Exception {
    JSONObject target = session.optJSONObject("relay");
    RelayTarget relay = target == null ? null : RelayTarget.fromJson(target);
    String origin = relay == null ? normalizeOrigin(session.getString("origin")) : relay.origin();
    return new ApiClient(origin, session.getString("token"), session.optString("pin"), relay,
        session.optString("stateKey", origin));
  }

  String address() { return relay == null ? origin : relay.endpoint; }

  ApiClient atUpdatedAddress(String input) throws Exception {
    String value = input.trim();
    if (value.startsWith("pocketagent://")) {
      PairingTicket ticket = PairingTicket.parse(value);
      if (!pin.equalsIgnoreCase(ticket.pin) || (relay == null) != (ticket.relay == null)
          || (relay != null && !relay.host.equals(ticket.relay.host))) {
        throw new IllegalArgumentException("This QR belongs to a different saved connection.");
      }
      value = ticket.relay == null ? ticket.origin : ticket.relay.endpoint;
    }
    if (relay != null) {
      if (value.startsWith("wss://")) value = "https://" + value.substring(6);
      if (value.endsWith("/relay")) value = value.substring(0, value.length() - 6);
    }
    String updated = normalizeOrigin(value);
    URI target = new URI(updated);
    URI previous = new URI(address());
    if (!"https".equals(target.getScheme())
        || (pin.isEmpty() && (!previous.getHost().equalsIgnoreCase(target.getHost())
            || "http".equals(previous.getScheme())))) {
      throw new IllegalArgumentException("This connection needs a fresh pairing to use that address.");
    }
    if (relay == null) return new ApiClient(updated, token, pin, null, stateKey);
    RelayTarget moved = new RelayTarget("wss" + updated.substring(5) + "/relay",
        relay.pin, relay.host, relay.device, relay.credential, relay.key);
    return new ApiClient(origin, token, pin, moved, stateKey);
  }

  void close() { if (connection != null) connection.close(); }

  void pauseRelay(boolean paused) { if (connection != null) connection.pause(paused); }

  static String normalizeOrigin(String input) throws Exception {
    String address = input.trim();
    URI uri = new URI(address.contains("://") ? address : "https://" + address);
    boolean development =
        BuildConfig.DEBUG
            && "http".equals(uri.getScheme())
            && ("10.0.2.2".equals(uri.getHost())
                || "127.0.0.1".equals(uri.getHost())
                || "localhost".equals(uri.getHost()));
    if ((!"https".equals(uri.getScheme()) && !development)
        || uri.getHost() == null
        || uri.getUserInfo() != null
        || uri.getQuery() != null
        || uri.getFragment() != null
        || (!uri.getPath().isEmpty() && !"/".equals(uri.getPath())))
      throw new IllegalArgumentException(
          "Enter an HTTPS server address without a path, query, or credentials.");
    return new URI(
            uri.getScheme(),
            null,
            uri.getHost().toLowerCase(java.util.Locale.ROOT),
            uri.getPort() == 443 ? -1 : uri.getPort(),
            null,
            null,
            null)
        .toASCIIString();
  }

  Object request(String path, JSONObject body) throws Exception {
    return request(path, body, 10, 75);
  }

  Object connectionRequest(String path, JSONObject body) throws Exception {
    return request(path, body, 6, 6);
  }

  private Object request(String path, JSONObject body, int connectSeconds, int readSeconds) throws Exception {
    if (this.connection != null) {
      RelayConnection.Reply reply = this.connection.request(path, body, token, false, readSeconds);
      return parseResponse(reply.status, new String(reply.body, StandardCharsets.UTF_8));
    }
    HttpURLConnection connection =
        (HttpURLConnection) new URI(origin + "/api" + path).toURL().openConnection();
    if (!pin.isEmpty()) {
      if (!(connection instanceof javax.net.ssl.HttpsURLConnection))
        throw new IllegalArgumentException("Pinned connections require HTTPS.");
      ((javax.net.ssl.HttpsURLConnection) connection)
          .setSSLSocketFactory(new PinnedTrust(pin).socketFactory());
      // Keep Android's hostname/SAN verification enabled. Only chain trust is replaced by the
      // scanned key pin.
    }
    connection.setInstanceFollowRedirects(false);
    connection.setConnectTimeout(connectSeconds * 1000);
    connection.setReadTimeout(readSeconds * 1000);
    connection.setUseCaches(false);
    connection.setRequestProperty("Accept", "application/json");
    if (!token.isEmpty()) connection.setRequestProperty("Authorization", "Bearer " + token);
    try {
      if (body != null) {
        connection.setRequestMethod("POST");
        connection.setDoOutput(true);
        connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
        connection.setRequestProperty("X-PocketAgent", "1");
        byte[] data = body.toString().getBytes(StandardCharsets.UTF_8);
        connection.setFixedLengthStreamingMode(data.length);
        try (java.io.OutputStream output = connection.getOutputStream()) {
          output.write(data);
        }
      }
      int status = connection.getResponseCode();
      if (status >= 300 && status < 400)
        throw new ApiError(
            status,
            "The server redirected the request. Enter its final HTTPS address; credentials were not"
                + " forwarded.");
      String text = "";
      InputStream stream =
          status >= 400 ? connection.getErrorStream() : connection.getInputStream();
      if (stream != null)
        try (InputStream input = stream;
            ByteArrayOutputStream output = new ByteArrayOutputStream()) {
          byte[] buffer = new byte[8192];
          int count;
          while ((count = input.read(buffer)) != -1) {
            if (output.size() + count > 8 * 1024 * 1024)
              throw new ApiError(413, "The conversation is too large to load.");
            output.write(buffer, 0, count);
          }
          text = output.toString(StandardCharsets.UTF_8.name());
        }
      return parseResponse(status, text);
    } finally {
      connection.disconnect();
    }
  }

  private static Object parseResponse(int status, String text) throws Exception {
    if (status >= 300 && status < 400)
      throw new ApiError(status, "The server redirected the request.");
    if (status >= 400) {
      String message = status == 401 ? "Device access ended. Pair with the desktop again." : "Request failed (" + status + ").";
      try { message = new JSONObject(text).optString("error", message); }
      catch (org.json.JSONException ignored) { }
      throw new ApiError(status, message);
    }
    return text.isEmpty() ? null : new JSONTokener(text).nextValue();
  }

  byte[] requestBytes(String path) throws Exception {
    if (this.connection != null) {
      RelayConnection.Reply reply = this.connection.request(path, null, token, true);
      if (reply.status >= 300) throw new ApiError(reply.status, "The image could not be loaded from the desktop.");
      return reply.body;
    }
    HttpURLConnection connection =
        (HttpURLConnection) new URI(origin + "/api" + path).toURL().openConnection();
    if (!pin.isEmpty()) {
      if (!(connection instanceof javax.net.ssl.HttpsURLConnection))
        throw new IllegalArgumentException("Pinned connections require HTTPS.");
      ((javax.net.ssl.HttpsURLConnection) connection)
          .setSSLSocketFactory(new PinnedTrust(pin).socketFactory());
    }
    connection.setInstanceFollowRedirects(false);
    connection.setConnectTimeout(10000);
    connection.setReadTimeout(30000);
    connection.setUseCaches(false);
    connection.setRequestProperty("Accept", "image/*");
    if (!token.isEmpty()) connection.setRequestProperty("Authorization", "Bearer " + token);
    try {
      int status = connection.getResponseCode();
      if (status >= 300 && status < 400) throw new ApiError(status, "The server redirected the image request.");
      if (status >= 400) throw new ApiError(status, "The image could not be loaded from the desktop.");
      try (InputStream input = connection.getInputStream(); ByteArrayOutputStream output = new ByteArrayOutputStream()) {
        byte[] buffer = new byte[8192];
        int count;
        while ((count = input.read(buffer)) != -1) {
          if (output.size() + count > 16 * 1024 * 1024) throw new ApiError(413, "That image is too large to view.");
          output.write(buffer, 0, count);
        }
        return output.toByteArray();
      }
    } finally {
      connection.disconnect();
    }
  }

  static String segment(String value) {
    return android.net.Uri.encode(value);
  }
}
