package dev.pocketagent;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import java.security.KeyStore;
import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;
import org.json.JSONObject;

final class SessionStore {
  private final SharedPreferences preferences;
  private static final String ALIAS = "PocketAgent.DeviceSession";

  SessionStore(Context context) {
    preferences = context.getSharedPreferences("connection", Context.MODE_PRIVATE);
  }

  private SecretKey key() throws Exception {
    KeyStore store = KeyStore.getInstance("AndroidKeyStore");
    store.load(null);
    if (!store.containsAlias(ALIAS)) {
      KeyGenerator generator =
          KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
      generator.init(
          new KeyGenParameterSpec.Builder(
                  ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
              .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
              .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
              .build());
      generator.generateKey();
    }
    return (SecretKey) store.getKey(ALIAS, null);
  }

  private synchronized void writeVault(JSONObject value) throws Exception {
    Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
    cipher.init(Cipher.ENCRYPT_MODE, key());
    String encrypted =
        Base64.encodeToString(
            cipher.doFinal(value.toString().getBytes(java.nio.charset.StandardCharsets.UTF_8)),
            Base64.NO_WRAP);
    if (!preferences
        .edit()
        .putString("session", encrypted)
        .putString("iv", Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP))
        .commit()) throw new java.io.IOException("Device session could not be saved.");
  }

  private synchronized JSONObject readVault() throws Exception {
    String value = preferences.getString("session", "");
    if (value.isEmpty()) return new JSONObject().put("servers", new org.json.JSONArray());
    Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
    cipher.init(
        Cipher.DECRYPT_MODE,
        key(),
        new GCMParameterSpec(128, Base64.decode(preferences.getString("iv", ""), Base64.NO_WRAP)));
    JSONObject vault =
        new JSONObject(
            new String(
                cipher.doFinal(Base64.decode(value, Base64.NO_WRAP)),
                java.nio.charset.StandardCharsets.UTF_8));
    if (vault.has("origin"))
      vault =
          new JSONObject()
              .put("servers", new org.json.JSONArray().put(vault))
              .put("active", vault.getString("origin"));
    return vault;
  }

  synchronized void save(String origin, String token) throws Exception {
    save(origin, token, new java.net.URI(origin).getHost());
  }

  synchronized void save(String origin, String token, String name) throws Exception {
    save(origin, token, name, "");
  }

  synchronized void save(String origin, String token, String name, String pin) throws Exception {
    JSONObject vault = readVault();
    org.json.JSONArray servers = vault.getJSONArray("servers");
    boolean updated = false;
    for (int i = 0; i < servers.length(); i++)
      if (origin.equals(servers.getJSONObject(i).optString("origin"))) {
        servers.put(
            i,
            new JSONObject()
                .put("origin", origin)
                .put("token", token)
                .put("name", name)
                .put("pin", pin));
        updated = true;
        break;
      }
    if (!updated)
      servers.put(
          new JSONObject()
              .put("origin", origin)
              .put("token", token)
              .put("name", name)
              .put("pin", pin));
    vault.put("active", origin);
    writeVault(vault);
    preferences.edit().putString("lastOrigin", origin).apply();
  }

  synchronized JSONObject load() throws Exception {
    JSONObject vault = readVault();
    org.json.JSONArray servers = vault.getJSONArray("servers");
    for (int i = 0; i < servers.length(); i++)
      if (servers.getJSONObject(i).optString("origin").equals(vault.optString("active")))
        return servers.getJSONObject(i);
    return servers.length() == 0 ? null : servers.getJSONObject(0);
  }

  synchronized org.json.JSONArray connections() throws Exception {
    org.json.JSONArray servers = readVault().getJSONArray("servers"),
        result = new org.json.JSONArray();
    for (int i = 0; i < servers.length(); i++) {
      JSONObject server = servers.getJSONObject(i);
      result.put(
          new JSONObject()
              .put("origin", server.getString("origin"))
              .put("name", server.optString("name", server.getString("origin"))));
    }
    return result;
  }

  synchronized JSONObject activate(String origin) throws Exception {
    JSONObject vault = readVault();
    vault.put("active", origin);
    writeVault(vault);
    return load();
  }

  synchronized void remove(String origin) throws Exception {
    JSONObject vault = readVault();
    org.json.JSONArray servers = vault.getJSONArray("servers"),
        remaining = new org.json.JSONArray();
    for (int i = 0; i < servers.length(); i++)
      if (!origin.equals(servers.getJSONObject(i).optString("origin")))
        remaining.put(servers.getJSONObject(i));
    vault
        .put("servers", remaining)
        .put(
            "active", remaining.length() > 0 ? remaining.getJSONObject(0).getString("origin") : "");
    writeVault(vault);
  }

  void clear() {
    try {
      JSONObject active = load();
      if (active != null) remove(active.getString("origin"));
    } catch (Exception ignored) {
      preferences.edit().remove("session").remove("iv").apply();
    }
  }

  String lastOrigin() {
    return preferences.getString("lastOrigin", "");
  }

  String selected(String origin) {
    return preferences.getString("selected:" + origin, "");
  }

  void select(String origin, String id) {
    preferences.edit().putString("selected:" + origin, id).apply();
  }

  boolean checkUpdatesOnStartup() {
    return preferences.getBoolean("checkUpdatesOnStartup", true);
  }

  void checkUpdatesOnStartup(boolean value) {
    preferences.edit().putBoolean("checkUpdatesOnStartup", value).apply();
  }

  String draft(String origin, String id) {
    return preferences.getString("draft:" + origin + ":" + id, "");
  }

  void draft(String origin, String id, String text) {
    preferences.edit().putString("draft:" + origin + ":" + id, text).apply();
  }

  boolean uncertain(String origin, String id) {
    return preferences.getBoolean("uncertain:" + origin + ":" + id, false);
  }

  boolean uncertain(String origin, String id, boolean value) {
    // Persist before network delivery so process death cannot lose the in-flight marker.
    return preferences.edit().putBoolean("uncertain:" + origin + ":" + id, value).commit();
  }
}
