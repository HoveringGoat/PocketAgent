package dev.pocketagent;

import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.qrcode.QRCodeReader;
import java.util.Collections;

final class QrDecoder {
  static String decode(byte[] luminance, int width, int height) throws Exception {
    QRCodeReader reader = new QRCodeReader();
    try {
      return reader
          .decode(
              new BinaryBitmap(
                  new HybridBinarizer(
                      new PlanarYUVLuminanceSource(
                          luminance, width, height, 0, 0, width, height, false))),
              Collections.singletonMap(DecodeHintType.TRY_HARDER, true))
          .getText();
    } finally {
      reader.reset();
    }
  }
}
