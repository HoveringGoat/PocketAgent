package dev.pocketagent;

import android.app.Instrumentation;
import android.os.Bundle;
import android.util.Base64;
import com.google.zxing.BarcodeFormat;
import com.google.zxing.common.BitMatrix;
import com.google.zxing.qrcode.QRCodeWriter;
import java.io.BufferedReader;
import java.io.ByteArrayInputStream;
import java.io.InputStreamReader;
import java.net.InetAddress;
import java.nio.charset.StandardCharsets;
import java.security.KeyStore;
import java.util.concurrent.atomic.AtomicInteger;
import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLServerSocket;
import javax.net.ssl.SSLSocket;
import org.json.JSONObject;

public final class PairingInstrumentation extends Instrumentation {
  private Bundle arguments;

  @Override
  public void onCreate(Bundle args) {
    arguments = args;
    start();
  }

  @Override
  public void onStart() {
    Bundle result = new Bundle();
    try {
      JSONObject fixture =
          new JSONObject(
              new String(
                  Base64.decode(arguments.getString("fixture"), Base64.DEFAULT),
                  StandardCharsets.UTF_8));
      String pin = fixture.getString("pin");
      if (fixture.has("desktopQr")) {
        byte[] encoded = Base64.decode(fixture.getString("desktopQr"), Base64.DEFAULT);
        android.graphics.Bitmap bitmap =
            android.graphics.BitmapFactory.decodeByteArray(encoded, 0, encoded.length);
        int width = bitmap.getWidth(), height = bitmap.getHeight();
        int[] colors = new int[width * height];
        bitmap.getPixels(colors, 0, width, 0, 0, width, height);
        byte[] gray = new byte[colors.length];
        for (int i = 0; i < gray.length; i++)
          gray[i] = (byte) android.graphics.Color.red(colors[i]);
        check(
            QrDecoder.decode(gray, width, height).equals(fixture.getString("desktopPayload")),
            "Desktop QR did not decode on Android");
        bitmap.recycle();
      }
      try (TlsServer server = new TlsServer(fixture.getString("valid"))) {
        String origin = "https://127.0.0.1:" + server.port();
        String payload =
            "pocketagent://pair?v=1&origin="
                + android.net.Uri.encode(origin)
                + "&spki="
                + pin
                + "&ticket="
                + new String(new char[64]).replace('\0', 'A');
        BitMatrix qr = new QRCodeWriter().encode(payload, BarcodeFormat.QR_CODE, 640, 640);
        byte[] pixels = new byte[640 * 640];
        for (int y = 0; y < 640; y++)
          for (int x = 0; x < 640; x++) pixels[y * 640 + x] = (byte) (qr.get(x, y) ? 0 : 255);
        PairingTicket ticket = PairingTicket.parse(QrDecoder.decode(pixels, 640, 640));
        check(
            ticket.origin.equals(origin) && ticket.pin.equals(pin) && ticket.ticket.length() == 64,
            "QR decoding/pairing payload mismatch");
        for (String invalid :
            new String[] {
              payload + "&spki=" + pin,
              payload.replace("v=1", "v=2"),
              payload.replace("https", "http"),
              "https://example.com",
              payload + "#fragment"
            }) {
          try {
            PairingTicket.parse(invalid);
            throw new AssertionError("Invalid pairing string accepted");
          } catch (IllegalArgumentException expected) {
          }
        }
        ApiClient client = new ApiClient(origin, "fixture-bearer", pin);
        JSONObject reply =
            (JSONObject)
                client.request("/devices/pair", new JSONObject().put("code", ticket.ticket));
        check(
            reply.getString("token").equals("fixture-token") && server.requests.get() == 1,
            "Pinned HTTPS positive request failed");
        int before = server.requests.get();
        reject(
            new ApiClient(
                origin, "secret-must-not-arrive", new String(new char[64]).replace('\0', '0')));
        reject(new ApiClient("https://127.0.0.2:" + server.port(), "secret-must-not-arrive", pin));
        check(
            server.requests.get() == before,
            "Credentials reached a mismatched identity or hostname");
        SessionStore store = new SessionStore(getTargetContext());
        store.save(origin, "fixture-token", "fixture", pin);
        check(store.load().getString("pin").equals(pin), "Encrypted saved pin missing");
        store.save("https://127.0.0.1:5147", "other", "other", pin);
        check(store.activate(origin).getString("pin").equals(pin), "Server switching lost pin");
        store.remove(origin);
        store.remove("https://127.0.0.1:5147");
        android.app.Activity activity =
            startActivitySync(
                new android.content.Intent(getTargetContext(), MainActivity.class)
                    .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK));
        runOnMainSync(
            () -> {
              android.view.View content = activity.findViewById(android.R.id.content);
              findInput(content, "Pairing string").setText(payload);
              findButton(content, "Connect").performClick();
            });
        long deadline = android.os.SystemClock.elapsedRealtime() + 10000;
        while (store.load() == null && android.os.SystemClock.elapsedRealtime() < deadline)
          Thread.sleep(50);
        check(
            store.load() != null && store.load().getString("pin").equals(pin),
            "Manual pairing UI did not persist the pinned session");
        runOnMainSync(activity::finish);
        store.remove(origin);
        result.putString(
            "stream",
            "PASS real QR decoding, strict payload parsing, pinned HTTPS, wrong-key/hostname"
                + " rejection before HTTP, encrypted pin restore and server switching\n");
      }
      try (TlsServer expired = new TlsServer(fixture.getString("expired"))) {
        reject(new ApiClient("https://127.0.0.1:" + expired.port(), "secret-must-not-arrive", pin));
        check(expired.requests.get() == 0, "Expired certificate received HTTP data");
      }
      result.putString(
          "stream",
          result.getString("stream") + "PASS expired certificate rejected without HTTP delivery\n");
      finish(-1, result);
    } catch (Throwable exception) {
      result.putString(
          "stream", "FAIL " + exception.getClass().getSimpleName() + ": " + exception.getMessage());
      finish(0, result);
    }
  }

  private static void check(boolean value, String message) {
    if (!value) throw new AssertionError(message);
  }

  private static android.widget.EditText findInput(android.view.View view, String description) {
    if (view instanceof android.widget.EditText
        && description.contentEquals(view.getContentDescription()))
      return (android.widget.EditText) view;
    if (view instanceof android.view.ViewGroup)
      for (int i = 0; i < ((android.view.ViewGroup) view).getChildCount(); i++) {
        android.widget.EditText found =
            findInput(((android.view.ViewGroup) view).getChildAt(i), description);
        if (found != null) return found;
      }
    return null;
  }

  private static android.widget.Button findButton(android.view.View view, String text) {
    if (view instanceof android.widget.Button
        && text.contentEquals(((android.widget.Button) view).getText()))
      return (android.widget.Button) view;
    if (view instanceof android.view.ViewGroup)
      for (int i = 0; i < ((android.view.ViewGroup) view).getChildCount(); i++) {
        android.widget.Button found =
            findButton(((android.view.ViewGroup) view).getChildAt(i), text);
        if (found != null) return found;
      }
    return null;
  }

  private static void reject(ApiClient client) throws Exception {
    try {
      client.request("/devices/pair", new JSONObject().put("code", "sensitive fixture"));
      throw new AssertionError("Invalid TLS identity accepted");
    } catch (javax.net.ssl.SSLException expected) {
    }
  }

  private static final class TlsServer implements AutoCloseable {
    final SSLServerSocket listener;
    final AtomicInteger requests = new AtomicInteger();

    TlsServer(String pfx) throws Exception {
      KeyStore keys = KeyStore.getInstance("PKCS12");
      keys.load(new ByteArrayInputStream(Base64.decode(pfx, Base64.DEFAULT)), new char[0]);
      KeyManagerFactory manager =
          KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
      manager.init(keys, new char[0]);
      SSLContext context = SSLContext.getInstance("TLS");
      context.init(manager.getKeyManagers(), null, null);
      listener =
          (SSLServerSocket)
              context
                  .getServerSocketFactory()
                  .createServerSocket(0, 10, InetAddress.getByName("0.0.0.0"));
      Thread thread =
          new Thread(
              () -> {
                while (!listener.isClosed()) {
                  try (SSLSocket socket = (SSLSocket) listener.accept()) {
                    socket.setSoTimeout(10000);
                    socket.startHandshake();
                    BufferedReader input =
                        new BufferedReader(
                            new InputStreamReader(socket.getInputStream(), StandardCharsets.UTF_8));
                    String line;
                    int length = 0;
                    while ((line = input.readLine()) != null && !line.isEmpty())
                      if (line.toLowerCase(java.util.Locale.ROOT).startsWith("content-length:"))
                        length = Integer.parseInt(line.substring(15).trim());
                    for (int i = 0; i < length; i++) if (input.read() < 0) break;
                    requests.incrementAndGet();
                    byte[] body =
                        "{\"token\":\"fixture-token\",\"authenticated\":true,\"version\":\"fixture\"}"
                            .getBytes(StandardCharsets.UTF_8);
                    socket
                        .getOutputStream()
                        .write(
                            ("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: "
                                    + body.length
                                    + "\r\nConnection: close\r\n\r\n")
                                .getBytes(StandardCharsets.US_ASCII));
                    socket.getOutputStream().write(body);
                    socket.getOutputStream().flush();
                  } catch (Exception ignored) {
                  }
                }
              });
      thread.setDaemon(true);
      thread.start();
    }

    int port() {
      return listener.getLocalPort();
    }

    @Override
    public void close() throws Exception {
      listener.close();
    }
  }
}
