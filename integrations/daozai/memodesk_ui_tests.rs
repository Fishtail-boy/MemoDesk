use super::{egui, H, ENV_LOCK};
use crate::app::{CALENDAR_PAGE, TODO_PAGE};
use crate::memodesk::{Bridge, ASSETS_PAGE, VAULT_PAGE};
use serde_json::json;

#[test]
fn tray_request_opens_the_vault_module() {
    let _lock = ENV_LOCK.lock().unwrap_or_else(|err| err.into_inner());
    let mut h = H::new();
    let mut bridge = Bridge::new(&h.ctx);
    bridge.set_test_snapshot(json!({"navigate": "vault"}));
    h.app.memo_bridge = Some(bridge);

    h.app.poll_memodesk(&h.ctx);

    assert_eq!(h.app.page(), VAULT_PAGE);
    assert!(h.app.open);
    assert!(!h.app.settings_open);
}

#[test]
fn digital_assets_is_a_swipeable_island_module() {
    let _lock = ENV_LOCK.lock().unwrap_or_else(|err| err.into_inner());
    let mut h = H::new();
    h.app.settings.visible_pages = [false, false, false, false, true, false, false, true, false];
    let mut bridge = Bridge::new(&h.ctx);
    bridge.set_test_assets(json!([{
        "id": "sample-server", "name": "华东云服务器", "category": "服务器", "description": "续费前检查实例规格",
        "link": "https://example.com/console", "expiration": "2026-12-20 20:00",
        "status": "今天到期", "expirationStatus": "DueSoon"
    }]));
    h.app.memo_bridge = Some(bridge);
    h.app.show_page(TODO_PAGE);

    let right = H::local(310.0, 74.0);
    let left = H::local(170.0, 74.0);
    h.hover(right, 0.9);
    let mouse = |pos, pressed| egui::Event::PointerButton {
        pos, button: egui::PointerButton::Primary, pressed, modifiers: Default::default(),
    };
    h.frame(vec![mouse(right, true)]);
    h.cursor = Some(left);
    h.frame(vec![egui::Event::PointerMoved(left)]);
    h.frame(vec![mouse(left, false)]);
    assert_eq!(h.app.page(), ASSETS_PAGE);

    h.hover(left, 0.65);
    let labels = h.shapes.iter().filter_map(|shape| match &shape.shape {
        egui::Shape::Text(text) if text.opacity_factor > 0.9 => Some(text.galley.text()),
        _ => None,
    }).collect::<Vec<_>>();
    for label in ["数字资产", "华东云服务器", "2026-12-20 20:00", "服务器 · 续费前检查实例规格", "https://example.com/console", "管理资产"] {
        assert!(labels.contains(&label), "Missing visible asset label: {label}");
    }

    let horizontal = |delta| egui::Event::MouseWheel {
        unit: egui::MouseWheelUnit::Point, delta: egui::vec2(delta, 0.0), modifiers: Default::default(),
    };
    h.frame(vec![horizontal(-26.0)]);
    assert_eq!(h.app.page(), VAULT_PAGE);
    h.frame(vec![horizontal(-26.0)]);
    assert_eq!(h.app.page(), VAULT_PAGE, "one trackpad swipe must advance just once");
    h.hover(left, 0.65);
    assert!(h.shapes.iter().any(|shape| matches!(&shape.shape, egui::Shape::Text(text) if text.galley.text() == "密码库")));
    h.click(H::local(398.0, 68.0));
    assert_eq!(h.app.page(), CALENDAR_PAGE);
    h.hover(left, 0.65);
    h.click(H::local(21.0, 68.0));
    assert_eq!(h.app.page(), VAULT_PAGE);
    h.hover(left, 0.3);
    h.frame(vec![horizontal(26.0)]);
    assert_eq!(h.app.page(), ASSETS_PAGE);
    h.hover(left, 0.65);
    h.click(H::local(21.0, 68.0));
    assert_eq!(h.app.page(), TODO_PAGE);
}
