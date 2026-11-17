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
}

impl RelayConfig {
    pub fn new() -> Self {
        Self {
            host_id: Uuid::new_v4(),
            host_token: Uuid::new_v4().to_string(),
            client_id: Uuid::new_v4(),
            client_token: Uuid::new_v4().to_string(),
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
    }
}
