package dev.pocketagent;

import android.Manifest;
import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.graphics.ImageFormat;
import android.hardware.camera2.CameraCaptureSession;
import android.hardware.camera2.CameraCharacteristics;
import android.hardware.camera2.CameraDevice;
import android.hardware.camera2.CameraManager;
import android.hardware.camera2.CaptureRequest;
import android.media.Image;
import android.media.ImageReader;
import android.os.Bundle;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Size;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.widget.LinearLayout;
import android.widget.TextView;
import java.nio.ByteBuffer;
import java.util.Arrays;
import java.util.Comparator;

@android.annotation.SuppressLint("SetTextI18n") // The native app currently has an English-only UI.
public final class QrScanActivity extends Activity implements SurfaceHolder.Callback {
  private SurfaceView preview;
  private TextView status;
  private HandlerThread worker;
  private Handler handler;
  private CameraDevice camera;
  private CameraCaptureSession session;
  private ImageReader reader;
  private volatile boolean active, opening, found;
  private long lastFrame;

  @Override
  public void onCreate(Bundle state) {
    super.onCreate(state);
    LinearLayout layout = new LinearLayout(this);
    layout.setOrientation(LinearLayout.VERTICAL);
    layout.setBackgroundColor(Color.rgb(17, 27, 23));
    status = new TextView(this);
    status.setTextColor(Color.rgb(193, 218, 166));
    status.setPadding(20, 20, 20, 20);
    status.setText(
        "Point the camera at the pairing QR on your own desktop. Images stay on this phone.");
    layout.addView(status);
    preview = new SurfaceView(this);
    preview.getHolder().addCallback(this);
    layout.addView(preview, new LinearLayout.LayoutParams(-1, 0, 1));
    setContentView(layout);
    if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED)
      requestPermissions(new String[] {Manifest.permission.CAMERA}, 1);
  }

  @Override
  protected void onResume() {
    super.onResume();
    active = true;
    worker = new HandlerThread("PairingCamera");
    worker.start();
    handler = new Handler(worker.getLooper());
    open();
  }

  @Override
  protected void onPause() {
    active = false;
    if (session != null) {
      session.close();
      session = null;
    }
    if (camera != null) {
      camera.close();
      camera = null;
    }
    if (reader != null) {
      reader.close();
      reader = null;
    }
    if (worker != null) {
      worker.quitSafely();
      worker = null;
    }
    opening = false;
    super.onPause();
  }

  @Override
  public void onRequestPermissionsResult(int request, String[] permissions, int[] results) {
    super.onRequestPermissionsResult(request, permissions, results);
    if (request == 1 && results.length > 0 && results[0] == PackageManager.PERMISSION_GRANTED)
      open();
    else
      status.setText(
          "Camera permission was declined. Go back and paste the desktop pairing string instead.");
  }

  @Override
  public void surfaceCreated(SurfaceHolder holder) {
    open();
  }

  @Override
  public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {}

  @Override
  public void surfaceDestroyed(SurfaceHolder holder) {}

  @SuppressWarnings("deprecation") // API 26 supports the camera2 list-of-surfaces session API.
  private void open() {
    if (!active
        || opening
        || camera != null
        || !preview.getHolder().getSurface().isValid()
        || checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED)
      return;
    opening = true;
    try {
      CameraManager manager = (CameraManager) getSystemService(CAMERA_SERVICE);
      String selected = null;
      for (String id : manager.getCameraIdList())
        if (Integer.valueOf(CameraCharacteristics.LENS_FACING_BACK)
            .equals(manager.getCameraCharacteristics(id).get(CameraCharacteristics.LENS_FACING))) {
          selected = id;
          break;
        }
      if (selected == null) throw new IllegalStateException("No back camera.");
      Size[] sizes =
          manager
              .getCameraCharacteristics(selected)
              .get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP)
              .getOutputSizes(ImageFormat.YUV_420_888);
      Size size =
          Arrays.stream(sizes)
              .min(
                  Comparator.comparingLong(
                      value -> Math.abs((long) value.getWidth() * value.getHeight() - 640L * 480)))
              .orElseThrow(IllegalStateException::new);
      preview.getHolder().setFixedSize(size.getWidth(), size.getHeight());
      reader =
          ImageReader.newInstance(size.getWidth(), size.getHeight(), ImageFormat.YUV_420_888, 2);
      reader.setOnImageAvailableListener(this::frame, handler);
      manager.openCamera(
          selected,
          new CameraDevice.StateCallback() {
            @Override
            public void onOpened(CameraDevice device) {
              opening = false;
              if (!active) {
                device.close();
                return;
              }
              camera = device;
              try {
                device.createCaptureSession(
                    Arrays.asList(preview.getHolder().getSurface(), reader.getSurface()),
                    new CameraCaptureSession.StateCallback() {
                      @Override
                      public void onConfigured(CameraCaptureSession configured) {
                        if (!active || camera == null || reader == null) {
                          configured.close();
                          return;
                        }
                        session = configured;
                        try {
                          CaptureRequest.Builder request =
                              camera.createCaptureRequest(CameraDevice.TEMPLATE_PREVIEW);
                          request.addTarget(preview.getHolder().getSurface());
                          request.addTarget(reader.getSurface());
                          request.set(
                              CaptureRequest.CONTROL_AF_MODE,
                              CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_PICTURE);
                          configured.setRepeatingRequest(request.build(), null, handler);
                        } catch (Exception exception) {
                          failed();
                        }
                      }

                      @Override
                      public void onConfigureFailed(CameraCaptureSession configured) {
                        configured.close();
                        failed();
                      }
                    },
                    handler);
              } catch (Exception exception) {
                failed();
              }
            }

            @Override
            public void onDisconnected(CameraDevice device) {
              device.close();
              failed();
            }

            @Override
            public void onError(CameraDevice device, int error) {
              device.close();
              failed();
            }
          },
          handler);
    } catch (Exception exception) {
      opening = false;
      failed();
    }
  }

  private void failed() {
    runOnUiThread(
        () -> {
          if (active)
            status.setText(
                "Camera unavailable. Go back and paste the pairing string, or reopen the scanner.");
        });
  }

  private void frame(ImageReader source) {
    try (Image image = source.acquireLatestImage()) {
      if (image == null
          || !active
          || found
          || android.os.SystemClock.elapsedRealtime() - lastFrame < 250) return;
      lastFrame = android.os.SystemClock.elapsedRealtime();
      int width = image.getWidth(), height = image.getHeight();
      Image.Plane plane = image.getPlanes()[0];
      ByteBuffer buffer = plane.getBuffer();
      int offset = buffer.position();
      byte[] data = new byte[width * height];
      for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
          data[y * width + x] =
              buffer.get(offset + y * plane.getRowStride() + x * plane.getPixelStride());
      String payload = QrDecoder.decode(data, width, height);
      PairingTicket.parse(payload);
      found = true;
      runOnUiThread(
          () -> {
            if (active) {
              setResult(RESULT_OK, new Intent().putExtra("pairing", payload));
              finish();
            }
          });
    } catch (Exception ignored) {
      /* A frame without a valid pairing QR is normal. No images or tickets are logged. */
    }
  }
}
