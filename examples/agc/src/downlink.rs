//! The downlink recorder (AGC_PLAN A7): Luminary emits 50 word-pairs/s on ch 034/035 paced by
//! DOWNRUPT; the bridge records them as NDJSON lines a player can watch with
//! `tail -f /var/log/agc/lm.downlink.ndjson | jq`. Word-pair framing: a new list starts with
//! the sync word carrying the word-order-code; we record raw pairs + the list id when the
//! first word matches a known Luminary099 downlink-list id (`DOWNLINK_LISTS.agc`).

use std::io::Write;

/// Known Luminary099 downlink-list ids (the first word of each 100-word frame).
pub fn list_name(id: u16) -> Option<&'static str> {
    Some(match id {
        0o77772 => "orbital-maneuvers",
        0o77773 => "coast-and-align",
        0o77774 => "rendezvous-prethrust",
        0o77775 => "descent-ascent",
        0o77776 => "lunar-surface-align",
        0o77777 => "agc-initialization-update",
        _ => return None,
    })
}

pub struct Downlink {
    out: Option<std::io::BufWriter<std::fs::File>>,
    ch34: Option<u16>,
    /// (list id, word index) of the frame in progress.
    pub current_list: Option<u16>,
    word_index: u32,
    pub pairs: u64,
}

impl Downlink {
    pub fn new(path: Option<&std::path::Path>) -> Self {
        let out = path.and_then(|p| {
            std::fs::OpenOptions::new()
                .create(true)
                .append(true)
                .open(p)
                .ok()
                .map(std::io::BufWriter::new)
        });
        Self { out, ch34: None, current_list: None, word_index: 0, pairs: 0 }
    }

    /// Feed a ch 034/035 write. Pairs are (034, 035); a 034 value that names a known list id
    /// starts a new frame.
    pub fn on_channel(&mut self, channel: u16, value: u16, ut: f64) {
        match channel {
            0o34 => {
                if list_name(value).is_some() {
                    self.current_list = Some(value);
                    self.word_index = 0;
                }
                self.ch34 = Some(value);
            }
            0o35 => {
                let w1 = self.ch34.take().unwrap_or(0);
                self.pairs += 1;
                self.word_index += 2;
                if let Some(o) = self.out.as_mut() {
                    let list = self
                        .current_list
                        .and_then(list_name)
                        .unwrap_or("unknown");
                    let _ = writeln!(
                        o,
                        "{{\"ut\":{ut:.2},\"list\":\"{list}\",\"i\":{},\"w1\":\"{w1:05o}\",\"w2\":\"{value:05o}\"}}",
                        self.word_index
                    );
                    if self.pairs % 50 == 0 {
                        let _ = o.flush();
                    }
                }
            }
            _ => {}
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn list_ids_decode() {
        assert_eq!(list_name(0o77775), Some("descent-ascent"));
        assert_eq!(list_name(0o12345), None);
    }

    #[test]
    fn records_pairs() {
        let dir = std::env::temp_dir().join(format!("agc-dl-{}", std::process::id()));
        std::fs::create_dir_all(&dir).unwrap();
        let p = dir.join("dl.ndjson");
        let mut d = Downlink::new(Some(&p));
        d.on_channel(0o34, 0o77775, 1.0);
        d.on_channel(0o35, 0o1234, 1.0);
        drop(d);
        let text = std::fs::read_to_string(&p).unwrap();
        assert!(text.contains("\"list\":\"descent-ascent\""), "{text}");
        assert!(text.contains("\"w2\":\"01234\""));
        std::fs::remove_dir_all(&dir).ok();
    }
}
