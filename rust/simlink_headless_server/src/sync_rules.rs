#![forbid(unsafe_code)]
//! Typed presentation edits. No script, native calls, or IO run on the actor.
use crate::messages as m;
use rhai::{Array, Engine, ImmutableString, Map, Scope, AST};
use std::collections::{BTreeMap, BTreeSet, VecDeque};

const MAX_PEERS: usize = 64;
const MAX_BINDINGS: usize = 1024;
#[derive(Clone, Debug, PartialEq, Eq)]
struct Resource {
    kind: u32,
    group: u32,
    instance: u64,
}
#[derive(Clone, Debug)]
enum Rule {
    Animation {
        player: String,
        resource: Resource,
        param: u32,
        from: String,
        to: String,
    },
    Audio {
        player: String,
        op: String,
        from: String,
        to: String,
    },
}
#[derive(Clone, Default)]
pub(crate) struct Rules(Vec<Rule>);
fn number<T: std::str::FromStr + ToString>(text: &str) -> Result<T, String> {
    let n: T = text
        .parse()
        .map_err(|_| "invalid numeric rule identifier")?;
    if n.to_string() != text {
        return Err("noncanonical numeric rule identifier".into());
    }
    Ok(n)
}
fn asset(text: &str) -> Result<(), String> {
    if text.is_empty()
        || text.len() > 128
        || !text
            .bytes()
            .all(|b| b.is_ascii_alphanumeric() || b"_-.:/".contains(&b))
    {
        return Err("asset names require 1..128 safe ASCII bytes".into());
    }
    Ok(())
}
fn take(fields: &mut BTreeMap<String, String>, key: &str) -> Result<String, String> {
    fields
        .remove(key)
        .ok_or_else(|| format!("rule needs {key}"))
}
impl Rule {
    fn player(&self) -> &str {
        match self {
            Self::Animation { player, .. } | Self::Audio { player, .. } => player,
        }
    }
    fn matches_player(&self, player: &str) -> bool {
        self.player() == "*" || self.player() == player
    }
    fn overlaps(&self, other: &Self) -> bool {
        if self.player() != "*" && other.player() != "*" && self.player() != other.player() {
            return false;
        }
        match (self, other) {
            (
                Self::Animation {
                    resource: a,
                    param: b,
                    from: c,
                    ..
                },
                Self::Animation {
                    resource: x,
                    param: y,
                    from: z,
                    ..
                },
            ) => a == x && b == y && c == z,
            (Self::Audio { op: a, from: b, .. }, Self::Audio { op: x, from: y, .. }) => {
                a == x && b == y
            }
            _ => false,
        }
    }
}
impl Rules {
    pub fn len(&self) -> usize {
        self.0.len()
    }
    pub fn compile(engine: &Engine, ast: &AST) -> Result<Self, String> {
        if !ast.iter_functions().any(|f| f.name == "rules") {
            return Ok(Self::default());
        }
        let entries = engine
            .call_fn::<Array>(&mut Scope::new(), ast, "rules", ())
            .map_err(|e| e.to_string())?;
        if entries.len() > 16 {
            return Err("rules exceed 16 entries".into());
        }
        let mut rules = Vec::new();
        for entry in entries {
            let map = entry.try_cast::<Map>().ok_or("each rule must be a map")?;
            let mut fields = BTreeMap::new();
            for (key, value) in map {
                let value = value
                    .try_cast::<ImmutableString>()
                    .ok_or("all rule values must be strings")?;
                fields.insert(key.to_string(), value.to_string());
            }
            let kind = take(&mut fields, "kind")?;
            let player = take(&mut fields, "player")?;
            if player != "*" && number::<u64>(&player)? == 0 {
                return Err("player must be positive ID text or *".into());
            }
            let from = take(&mut fields, "from")?;
            let to = take(&mut fields, "to")?;
            asset(&from)?;
            asset(&to)?;
            if from == to {
                return Err("identity rule is redundant; omit it".into());
            }
            let rule = match kind.as_str() {
                "anim_param" => {
                    let resource = Resource {
                        kind: number(&take(&mut fields, "machine_type")?)?,
                        group: number(&take(&mut fields, "machine_group")?)?,
                        instance: number(&take(&mut fields, "machine_instance")?)?,
                    };
                    let param = number(&take(&mut fields, "param")?)?;
                    if resource.kind == 0 || resource.instance == 0 || param == 0 {
                        return Err("animation identifiers must be nonzero".into());
                    }
                    Rule::Animation {
                        player,
                        resource,
                        param,
                        from,
                        to,
                    }
                }
                "audio_name" => {
                    let op = take(&mut fields, "op")?;
                    if !matches!(op.as_str(), "o" | "w") {
                        return Err("audio_name supports only o/w starts".into());
                    }
                    Rule::Audio {
                        player,
                        op,
                        from,
                        to,
                    }
                }
                _ => return Err("unsupported rule kind".into()),
            };
            if !fields.is_empty() {
                return Err("unknown rule key".into());
            }
            if rules.iter().any(|previous: &Rule| previous.overlaps(&rule)) {
                return Err("overlapping rule selectors".into());
            }
            rules.push(rule);
        }
        Ok(Self(rules))
    }
}
#[derive(Clone)]
struct Driver {
    resource: Resource,
    host: u64,
}
#[derive(Clone)]
struct Sound {
    object: u64,
    op: String,
    source: String,
    target: String,
}
#[derive(Default)]
struct Peer {
    drivers: BTreeMap<u32, Driver>,
    sounds: BTreeMap<u32, Sound>,
    untrusted_audio: bool,
}
#[derive(Clone, Default)]
pub(crate) struct Effects {
    animation: u64,
    audio: u64,
    baseline: u64,
    malformed: u64,
    unknown: u64,
    capacity: u64,
    players: BTreeMap<String, (u64, u64)>,
    last: VecDeque<String>,
    output_fallbacks: u64,
    uncertain_audio_skips: u64,
}
#[derive(Default)]
pub(crate) struct Mapper {
    peers: BTreeMap<u64, Peer>,
    stats: Effects,
}
impl Mapper {
    pub fn checkpoint(&self) -> Effects {
        self.stats.clone()
    }
    pub fn rollback_stats(&mut self, stats: Effects) {
        self.stats = stats;
    }
    pub fn output_fallback(&mut self) {
        self.stats.output_fallbacks += 1;
    }
    pub fn invalidate(&mut self, connection: u64) {
        if self.peers.contains_key(&connection) || self.peers.len() < MAX_PEERS {
            let peer = self.peers.entry(connection).or_default();
            peer.drivers.clear();
            peer.sounds.clear();
            peer.untrusted_audio = true;
        }
    }
    pub fn reset_world(&mut self) {
        self.peers.clear();
    }
    pub fn observe_original(&mut self, connection: u64, player: &str, line: &str, protected: bool) {
        self.apply_inner(connection, player, line, protected, &Rules::default(), true);
    }
    pub fn retain(&mut self, connections: &BTreeSet<u64>) {
        self.peers.retain(|id, _| connections.contains(id));
    }
    pub fn reset(&mut self) {
        for peer in self.peers.values_mut() {
            peer.drivers.clear();
        }
    }
    /// One source record is visited once. Return None to keep its exact original bytes.
    pub fn apply(
        &mut self,
        connection: u64,
        player: &str,
        line: &str,
        protected: bool,
        rules: &Rules,
    ) -> Option<String> {
        self.apply_inner(connection, player, line, protected, rules, false)
    }
    fn apply_inner(
        &mut self,
        connection: u64,
        player: &str,
        line: &str,
        protected: bool,
        rules: &Rules,
        force_source: bool,
    ) -> Option<String> {
        let f: Vec<_> = line.split('|').collect();
        if f.len() < 3 || !matches!(f[1], "A" | "X" | "Z" | "Ps" | "Au" | "Ne") {
            return None;
        }
        if !self.peers.contains_key(&connection) && self.peers.len() >= MAX_PEERS {
            self.stats.capacity += 1;
            return None;
        }
        let peer = self.peers.entry(connection).or_default();
        let mut changed = None;
        match f[1] {
            "Ne" => {
                if f.len() == 3 && f[2].parse::<u64>().is_ok() {
                    peer.drivers.clear();
                }
            }
            "A" => {
                if let Ok(handle) = f[2].parse::<u32>() {
                    peer.drivers.remove(&handle);
                    let parsed = (|| {
                        if f.len() != 8 || handle == 0 {
                            return None;
                        }
                        Some(Driver {
                            host: f[3].parse().ok()?,
                            resource: Resource {
                                kind: f[4].parse().ok()?,
                                group: f[5].parse().ok()?,
                                instance: f[6].parse().ok()?,
                            },
                        })
                    })();
                    if let Some(driver) = parsed {
                        if f[7].parse::<i32>().is_ok()
                            && driver.resource.kind != 0
                            && driver.resource.instance != 0
                        {
                            if peer.drivers.len() < MAX_BINDINGS {
                                peer.drivers.insert(handle, driver);
                            } else {
                                self.stats.capacity += 1;
                            }
                        } else {
                            self.stats.malformed += 1;
                        }
                    } else {
                        self.stats.malformed += 1;
                    }
                } else {
                    self.stats.malformed += 1;
                }
            }
            "X" => {
                if f.len() == 3 {
                    if let Ok(handle) = f[2].parse::<u32>() {
                        peer.drivers.remove(&handle);
                    }
                }
            }
            "Z" => {
                if f.len() == 3 {
                    if let Ok(host) = f[2].parse::<u64>() {
                        peer.drivers.retain(|_, d| d.host != host);
                    }
                }
            }
            "Ps" => {
                if !rules
                    .0
                    .iter()
                    .any(|r| matches!(r, Rule::Animation { .. }) && r.matches_player(player))
                {
                    return None;
                }
                if f.len() != 5 || f[2].parse::<u32>().is_err() || f[3].parse::<u32>().is_err() {
                    self.stats.malformed += 1;
                    return None;
                }
                let handle = f[2].parse::<u32>().expect("checked");
                let Some(driver) = peer.drivers.get(&handle) else {
                    self.stats.unknown += 1;
                    return None;
                };
                let param = f[3].parse::<u32>().expect("checked");
                for rule in &rules.0 {
                    if let Rule::Animation {
                        resource,
                        param: wanted,
                        from,
                        to,
                        ..
                    } = rule
                    {
                        if rule.matches_player(player)
                            && resource == &driver.resource
                            && *wanted == param
                            && from == f[4]
                        {
                            changed = Some((4, to.clone(), false));
                            break;
                        }
                    }
                }
            }
            "Au" => {
                if f.len() < 4 {
                    self.stats.malformed += 1;
                    return None;
                }
                let Ok(object) = f[2].parse::<u64>() else {
                    self.stats.malformed += 1;
                    return None;
                };
                match f[3] {
                    "o" | "w" => {
                        if f.len() != 7
                            || object == 0
                            || f[4].parse::<u32>().ok().is_none_or(|h| h == 0)
                            || (f[3] == "o" && !matches!(f[6], "0" | "1"))
                            || (f[3] == "w" && f[6].parse::<u32>().is_err())
                        {
                            self.stats.malformed += 1;
                            return None;
                        }
                        let handle = f[4].parse::<u32>().expect("checked");
                        // Recorder names use native BridgeLineCodec hex (both cases),
                        // distinct from the public protocol's lowercase scalar codec.
                        let Some(source) = simlink_bridge::text::decode_hex_bytes(f[5])
                            .ok()
                            .and_then(|bytes| String::from_utf8(bytes).ok())
                        else {
                            // Native replay may have consumed a different source lifetime;
                            // do not reuse an earlier alias after an undecodable start.
                            peer.sounds.remove(&handle);
                            peer.untrusted_audio = true;
                            self.stats.malformed += 1;
                            return None;
                        };
                        let cached = (f[0].parse::<u64>() == Ok(0))
                            .then(|| peer.sounds.get(&handle))
                            .flatten()
                            .filter(|s| s.object == object && s.op == f[3] && s.source == source);
                        let target = if force_source
                            || (peer.untrusted_audio
                                && f[0].parse::<u64>() == Ok(0)
                                && cached.is_none())
                        {
                            if !force_source {
                                self.stats.uncertain_audio_skips += 1;
                            }
                            source.clone()
                        } else if let Some(cached) = cached {
                            cached.target.clone()
                        } else {
                            rules
                                .0
                                .iter()
                                .find_map(|rule| match rule {
                                    Rule::Audio { op, from, to, .. }
                                        if rule.matches_player(player)
                                            && op == f[3]
                                            && from == &source =>
                                    {
                                        Some(to.clone())
                                    }
                                    _ => None,
                                })
                                .unwrap_or_else(|| source.clone())
                        };
                        if peer.sounds.contains_key(&handle) || peer.sounds.len() < MAX_BINDINGS {
                            peer.sounds.insert(
                                handle,
                                Sound {
                                    object,
                                    op: f[3].into(),
                                    source: source.clone(),
                                    target: target.clone(),
                                },
                            );
                            if target != source {
                                changed = Some((5, m::hex_encode(&target), true));
                            }
                        } else {
                            self.stats.capacity += 1;
                        }
                    }
                    "x" => {
                        if f.len() == 5 {
                            if let Ok(handle) = f[4].parse::<u32>() {
                                if peer.sounds.get(&handle).is_some_and(|s| s.object == object) {
                                    peer.sounds.remove(&handle);
                                }
                            }
                        }
                    }
                    "z" => {
                        if f.len() == 4 {
                            peer.sounds.retain(|_, s| s.object != object);
                        }
                    }
                    // Other start families share the native handle namespace; never carry an old o/w alias over them.
                    "a" | "t" => {
                        if let Some(handle) = f.get(4).and_then(|v| v.parse::<u32>().ok()) {
                            peer.sounds.remove(&handle);
                        }
                    }
                    _ => {}
                }
            }
            _ => {}
        }
        let (index, value, audio) = changed?;
        let ids = if audio {
            format!("object={}|handle={}|op={}", f[2], f[4], f[3])
        } else {
            format!("driver={}|param={}", f[2], f[3])
        };
        let from = if audio {
            f[5].to_string()
        } else {
            m::hex_encode(f[4])
        };
        let to = if audio {
            value.clone()
        } else {
            m::hex_encode(&value)
        };
        if self.stats.last.len() == 16 {
            self.stats.last.pop_front();
        }
        self.stats.last.push_back(format!("last_transform|player={player}|kind={}|source_seq={}|{ids}|from_hex={from}|to_hex={to}",f[1],f[0]));

        let mut output = f.iter().map(|s| (*s).to_string()).collect::<Vec<_>>();
        output[index] = value;
        if audio {
            self.stats.audio += 1;
        } else {
            self.stats.animation += 1;
        }
        if protected || f[0].parse::<u64>() == Ok(0) {
            self.stats.baseline += 1;
        }
        if self.stats.players.contains_key(player) || self.stats.players.len() < MAX_PEERS {
            let count = self.stats.players.entry(player.into()).or_default();
            if audio {
                count.1 += 1;
            } else {
                count.0 += 1;
            }
        }
        Some(output.join("|"))
    }
    pub fn report(&self) -> Vec<String> {
        let mut rows=vec![format!("transforms|animation_values={}|audio_names={}|baseline_values={}|malformed_records={}|unknown_drivers={}|capacity_skips={}",self.stats.animation,self.stats.audio,self.stats.baseline,self.stats.malformed,self.stats.unknown,self.stats.capacity)];
        rows.extend(self.stats.players.iter().map(|(p, (a, s))| {
            format!("transform|player={p}|animation_values={a}|audio_names={s}")
        }));
        rows[0].push_str(&format!(
            "|output_fallbacks={}|uncertain_audio_skips={}",
            self.stats.output_fallbacks, self.stats.uncertain_audio_skips
        ));
        rows.extend(self.stats.last.iter().cloned());
        rows
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn rules(target: &str) -> Rules {
        let e = Engine::new_raw();
        let source = format!(
            r#"fn target() {{"{target}"}} fn rules() {{[
          #{{kind:"anim_param",player:"*",machine_type:"7",machine_group:"8",machine_instance:"9",param:"10",from:"clip_a",to:target()}},
          #{{kind:"audio_name",player:"*",op:"o",from:"sound_a",to:target()}}
        ]}}"#
        );
        Rules::compile(&e, &e.compile(source).unwrap()).unwrap()
    }
    #[test]
    fn first_uninspectable_batch_marks_unknown_synthetic_audio_untrusted() {
        let mut m = Mapper::default();
        m.invalidate(7);
        let start = format!("0|Au|1|o|55|{}|1", m::hex_encode("sound_a"));
        assert!(m.apply(7, "2", &start, true, &rules("new")).is_none());
        let live = start.replacen("0|", "1|", 1);
        assert!(m.apply(7, "2", &live, false, &rules("new")).is_some());
    }
    #[test]
    fn nav_epoch_keeps_audio_lifetime_but_world_replacement_resets_all_metadata() {
        let mut m = Mapper::default();
        let old = rules("old");
        let new = rules("new");
        m.apply(
            1,
            "18446744073709551615",
            "0|A|7|18446744073709551615|7|8|9|-2",
            true,
            &old,
        );
        let sound = format!(
            "1|Au|18446744073709551615|o|55|{}|1",
            m::hex_encode("sound_a")
        );
        assert!(m
            .apply(1, "18446744073709551615", &sound, false, &old)
            .unwrap()
            .contains(&m::hex_encode("old")));
        m.reset();
        assert!(m.apply(1, "2", "2|Ps|7|10|clip_a", false, &new).is_none());
        let baseline = sound.replacen("1|", "0|", 1);
        assert!(m
            .apply(1, "2", &baseline, true, &new)
            .unwrap()
            .contains(&m::hex_encode("old")));
        m.reset_world();
        assert!(m
            .apply(1, "2", &baseline, true, &new)
            .unwrap()
            .contains(&m::hex_encode("new")));
        assert!(m.apply(1, "2", "3|Ps|7|10|clip_a", false, &new).is_none());
    }
    #[test]
    fn rules_validate_whole_table_unknown_keys_and_wildcard_conflicts() {
        let e = Engine::new_raw();
        let good = r#"#{kind:"audio_name",player:"*",op:"w",from:"original",to:"alternate"}"#;
        for bad in [
            r#"#{kind:"audio_name",player:"2",op:"w",from:"original",to:"other"}"#,
            r#"#{kind:"audio_name",player:"2",op:"x",from:"a",to:"b"}"#,
            r#"#{kind:"audio_name",player:"2",op:"o",from:"a",to:"bad|name"}"#,
            r#"#{kind:"audio_name",player:"02",op:"o",from:"a",to:"b"}"#,
            r#"#{kind:"audio_name",player:"2",op:"o",from:"a",to:"b",extra:"no"}"#,
            r#"#{kind:"audio_name",player:2,op:"o",from:"a",to:"b"}"#,
        ] {
            let ast = e.compile(format!("fn rules() {{[{good},{bad}]}}")).unwrap();
            assert!(Rules::compile(&e, &ast).is_err(), "{bad}");
        }
        let ast = e
            .compile(format!("fn rules() {{[{}]}}", vec![good; 17].join(",")))
            .unwrap();
        assert!(Rules::compile(&e, &ast).is_err());
        for source in ["fn rules() {17}", "fn rules(x) {[]}"] {
            assert!(Rules::compile(&e, &e.compile(source).unwrap()).is_err());
        }
    }
    #[test]
    fn unsupported_audio_operations_and_nonmatching_machine_are_unchanged() {
        let mut m = Mapper::default();
        let r = rules("replacement");
        m.apply(1, "2", "1|A|7|1|7|8|10|-2", false, &r);
        assert!(m.apply(1, "2", "2|Ps|7|10|clip_a", false, &r).is_none());
        for line in [
            "3|Au|1|q|55|0.5",
            "4|Au|1|A|55|anything",
            "5|Au|0|t|55|1|2|3|6161",
            "6|Au|1|s|55|0|0.5",
        ] {
            assert!(m.apply(1, "2", line, false, &r).is_none());
        }
    }
}
