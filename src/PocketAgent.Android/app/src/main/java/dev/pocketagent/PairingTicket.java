package dev.pocketagent;

import android.net.Uri;

final class PairingTicket {
  final String origin, pin, ticket;

  private PairingTicket(String origin, String pin, String ticket) {
    this.origin = origin;
    this.pin = pin;
    this.ticket = ticket;
  }

  static PairingTicket parse(String value) throws Exception {
    if (value.length() > 2048) throw new IllegalArgumentException("Pairing string is too long.");
    Uri uri = Uri.parse(value.trim());
    if (!"pocketagent".equals(uri.getScheme())
        || !"pair".equals(uri.getAuthority())
        || (uri.getPath() != null && !uri.getPath().isEmpty())
        || uri.getFragment() != null
        || uri.getQueryParameterNames().size() != 4)
      throw new IllegalArgumentException(
          "Use a PocketAgent pairing QR or the full pairing string from the desktop.");
    for (String field : new String[] {"v", "origin", "spki", "ticket"})
      if (uri.getQueryParameters(field).size() != 1)
        throw new IllegalArgumentException("Invalid pairing string fields.");
    String pin = uri.getQueryParameter("spki"), ticket = uri.getQueryParameter("ticket");
    if (!"1".equals(uri.getQueryParameter("v"))
        || pin == null
        || !pin.matches("[A-Fa-f0-9]{64}")
        || ticket == null
        || !ticket.matches("[A-Fa-f0-9]{64}"))
      throw new IllegalArgumentException("Invalid or unsupported pairing string.");
    String origin = ApiClient.normalizeOrigin(uri.getQueryParameter("origin"));
    if (!origin.startsWith("https://"))
      throw new IllegalArgumentException("QR pairing requires HTTPS.");
    return new PairingTicket(origin, pin.toUpperCase(java.util.Locale.ROOT), ticket);
  }
}
