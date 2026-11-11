package dev.pocketagent;

import java.security.MessageDigest;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

// Deliberate app-owned trust: the scanned key is the trust anchor, not an arbitrary server
// certificate.
// checkServerTrusted verifies the exact key, validity and EKU; HttpsURLConnection still verifies
// SAN.
@android.annotation.SuppressLint("CustomX509TrustManager")
final class PinnedTrust implements X509TrustManager {
  private final byte[] expected;

  PinnedTrust(String pin) {
    if (!pin.matches("[A-Fa-f0-9]{64}"))
      throw new IllegalArgumentException("Invalid saved server identity. Pair again.");
    expected = new byte[32];
    for (int i = 0; i < expected.length; i++)
      expected[i] = (byte) Integer.parseInt(pin.substring(i * 2, i * 2 + 2), 16);
  }

  SSLSocketFactory socketFactory() throws Exception {
    SSLContext context = SSLContext.getInstance("TLS");
    context.init(null, new TrustManager[] {this}, null);
    return context.getSocketFactory();
  }

  @Override
  public void checkServerTrusted(X509Certificate[] chain, String authType)
      throws CertificateException {
    if (chain == null || chain.length == 0)
      throw new CertificateException("Missing desktop certificate.");
    try {
      if (!MessageDigest.isEqual(
          expected,
          MessageDigest.getInstance("SHA-256").digest(chain[0].getPublicKey().getEncoded())))
        throw new CertificateException(
            "Desktop identity changed. Scan its QR again; connection rejected.");
      chain[0].checkValidity();
      java.util.List<String> purposes = chain[0].getExtendedKeyUsage();
      if (chain[0].getBasicConstraints() >= 0
          || purposes == null
          || !purposes.contains("1.3.6.1.5.5.7.3.1"))
        throw new CertificateException("Invalid desktop server certificate.");
    } catch (CertificateException exception) {
      throw exception;
    } catch (Exception exception) {
      throw new CertificateException("Could not verify desktop identity.", exception);
    }
  }

  @Override
  public void checkClientTrusted(X509Certificate[] chain, String authType)
      throws CertificateException {
    throw new CertificateException("Client trust is not supported.");
  }

  @Override
  public X509Certificate[] getAcceptedIssuers() {
    return new X509Certificate[0];
  }
}
