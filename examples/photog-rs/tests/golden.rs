use std::path::PathBuf;

use photog::{compile_project, load_project, CompileRequest};

#[test]
fn example_trailer_compiles_to_golden_track_and_cues() {
    let root = PathBuf::from(env!("CARGO_MANIFEST_DIR"));
    let project = load_project(root.join("projects/example-trailer.photog.json")).unwrap();
    let request = CompileRequest {
        track_name: "trailer-track".into(),
        cue_id: "trailer-cues".into(),
        group_id: "trailer-group".into(),
        start_shot: 0,
    };
    let take = compile_project(&project, &request).unwrap();

    if std::env::var_os("UPDATE_GOLDEN").is_some() {
        std::fs::write(
            root.join("tests/golden/example-trailer.track.json"),
            &take.track_json,
        )
        .unwrap();
        std::fs::write(
            root.join("tests/golden/example-trailer.timed_batch"),
            &take.timed_batch,
        )
        .unwrap();
    }

    assert_eq!(
        take.track_json,
        include_str!("golden/example-trailer.track.json")
    );
    assert_eq!(
        take.timed_batch,
        include_str!("golden/example-trailer.timed_batch")
    );
    assert_eq!(take.duration_s, 25.0);
    assert_eq!(take.warnings.len(), 1);
}
