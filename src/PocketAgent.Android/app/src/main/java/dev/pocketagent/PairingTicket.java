package dev.pocketagent;

import android.net.Uri;

final class PairingTicket {
  final String origin, pin, ticket;
  final RelayTarget relay;

  private PairingTicket(String origin, String pin, String ticket, RelayTarget relay) {
    this.origin = origin;
    this.pin = pin;
    this.ticket = ticket;
    this.relay = relay;
  }

  static PairingTicket parse(String value) throws Exception {
    if (value.length() > 2048) throw new IllegalArgumentException("Pairing string is too long.");
    Uri uri = Uri.parse(value.trim());
    boolean relayed = "pair-relay".equals(uri.getAuthority());
    if (!"pocketagent".equals(uri.getScheme())
        || (!relayed && !"pair".equals(uri.getAuthority()))
        || (uri.getPath() != null && !uri.getPath().isEmpty())
        || uri.getFragment() != null)
      throw new IllegalArgumentException(
          "Use a PocketAgent pairing QR or the full pairing string from the desktop.");
    String[] fields = relayed ? new String[] {"v", "origin", "spki", "host", "device", "auth", "key", "ticket"}
        : new String[] {"v", "origin", "spki", "ticket"};
    if (uri.getQueryParameterNames().size() != fields.length)
      throw new IllegalArgumentException("Invalid pairing string fields.");
    for (String field : fields)
      if (uri.getQueryParameters(field).size() != 1)
        throw new IllegalArgumentException("Invalid pairing string fields.");
    String pin = uri.getQueryParameter("spki"), ticket = uri.getQueryParameter("ticket");
    if (!"1".equals(uri.getQueryParameter("v"))
        || pin == null
        || ((!relayed || !pin.isEmpty()) && !pin.matches("[A-Fa-f0-9]{64}"))
        || ticket == null
        || !ticket.matches("[A-Fa-f0-9]{64}"))
      throw new IllegalArgumentException("Invalid or unsupported pairing string.");
    if (relayed) {
      RelayTarget target = new RelayTarget(uri.getQueryParameter("origin"), pin,
          uri.getQueryParameter("host"), uri.getQueryParameter("device"),
          uri.getQueryParameter("auth"), uri.getQueryParameter("key"));
      return new PairingTicket(target.origin(), pin, ticket, target);
    }
    String origin = ApiClient.normalizeOrigin(uri.getQueryParameter("origin"));
    if (!origin.startsWith("https://"))
      throw new IllegalArgumentException("QR pairing requires HTTPS.");
    return new PairingTicket(origin, pin.toUpperCase(java.util.Locale.ROOT), ticket, null);
  }

  ApiClient client(String deviceToken) { return new ApiClient(origin, deviceToken, pin, relay); }
}
