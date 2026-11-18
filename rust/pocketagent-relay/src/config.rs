use pocketagent_crypto::SessionKey;
use serde::{Deserialize, Serialize};
use uuid::Uuid;

/// Development relay identities. Tokens are intentionally represented as strings
/// so a probe config can be reviewed and moved between local test processes.
/// Production enrollment must replace this with protected storage.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq, Eq)]
pub struct RelayConfig {
    pub host_id: Uuid,
    pub host_token: String,
    pub client_id: Uuid,
    pub client_token: String,
    #[serde(default)]
    pub session_key: String,
}

impl RelayConfig {
    pub fn new() -> Self {
        Self {
            host_id: Uuid::new_v4(),
            host_token: Uuid::new_v4().to_string(),
            client_id: Uuid::new_v4(),
            client_token: Uuid::new_v4().to_string(),
            session_key: SessionKey::random().to_wire(),
        }
    }
}

impl Default for RelayConfig {
    fn default() -> Self {
        Self::new()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn config_json_round_trip_preserves_identities_and_tokens() {
        let config = RelayConfig::new();
        let encoded = serde_json::to_string_pretty(&config).expect("config should serialize");
        let decoded: RelayConfig = serde_json::from_str(&encoded).expect("config should parse");
        assert_eq!(decoded, config);
        assert!(SessionKey::from_wire(&decoded.session_key).is_some());
    }

    #[test]
    fn old_config_without_session_key_can_be_migrated() {
        let config = RelayConfig::new();
        let mut value = serde_json::to_value(config).expect("config should serialize");
        value
            .as_object_mut()
            .expect("config should be an object")
            .remove("session_key");
        let mut migrated: RelayConfig = serde_json::from_value(value).expect("old config parses");
        if migrated.session_key.is_empty() {
            migrated.session_key = SessionKey::random().to_wire();
        }
        assert!(SessionKey::from_wire(&migrated.session_key).is_some());
    }
}
