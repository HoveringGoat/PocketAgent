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
  final String token;
  final String pin;

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
    this.origin = origin;
    this.token = token;
    this.pin = pin;
  }

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
    connection.setConnectTimeout(10000);
    connection.setReadTimeout(75000);
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
      if (status >= 400) {
        String message =
            status == 401
                ? "Device access ended. Pair with the desktop again."
                : "Request failed (" + status + ").";
        try {
          message = new JSONObject(text).optString("error", message);
        } catch (org.json.JSONException ignored) {
        }
        throw new ApiError(status, message);
      }
      return text.isEmpty() ? null : new JSONTokener(text).nextValue();
    } finally {
      connection.disconnect();
    }
  }

  static String segment(String value) {
    return android.net.Uri.encode(value);
  }
}
