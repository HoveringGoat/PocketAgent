package dev.pocketagent;

import java.net.URI;
import java.util.Locale;
import java.util.UUID;
import org.json.JSONObject;

final class RelayTarget {
  final String endpoint, pin, host, device, credential, key;

  RelayTarget(String endpoint, String pin, String host, String device, String credential, String key)
      throws Exception {
    URI uri = new URI(endpoint);
    if (!"wss".equals(uri.getScheme()) || uri.getHost() == null || uri.getUserInfo() != null
        || !"/relay".equals(uri.getRawPath()) || uri.getRawQuery() != null || uri.getRawFragment() != null
        || uri.getPort() == 0 || uri.getPort() > 65535)
      throw new IllegalArgumentException("Phone relay pairing requires a secure relay address.");
    this.endpoint = uri.toASCIIString();
    this.pin = pin;
    this.host = uuid(host);
    this.device = uuid(device);
    this.credential = credential;
    this.key = key;
    if ((!pin.isEmpty() && !pin.matches("[A-Fa-f0-9]{64}"))
        || !credential.matches("[A-Fa-f0-9]{64}") || !key.matches(device + ":[A-Fa-f0-9]{64}"))
      throw new IllegalArgumentException("Invalid relay pairing credentials.");
  }

  private static String uuid(String value) {
    String normalized = UUID.fromString(value).toString();
    if (!normalized.equals(value.toLowerCase(Locale.ROOT)))
      throw new IllegalArgumentException("Invalid relay identity.");
    return normalized;
  }

  String origin() {
    // Local identity for saved tabs and conversation state. The endpoint is a separate field.
    return "pocketagent://relay/" + host;
  }

  JSONObject toJson() throws Exception {
    return new JSONObject().put("endpoint", endpoint).put("pin", pin).put("host", host)
        .put("device", device).put("credential", credential).put("key", key);
  }

  static RelayTarget fromJson(JSONObject value) throws Exception {
    return new RelayTarget(value.getString("endpoint"), value.getString("pin"), value.getString("host"),
        value.getString("device"), value.getString("credential"), value.getString("key"));
  }
}
