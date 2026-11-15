use crate::{app::{App, TODO_PAGE, QUOTA_PAGE}, model::TodoItem};
use eframe::egui;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::io::{self, BufRead, Write};
use std::collections::HashSet;
use std::sync::mpsc::{self, Receiver, Sender};
use std::time::{Duration, Instant};

pub fn managed() -> bool {
    static MANAGED: std::sync::OnceLock<bool> = std::sync::OnceLock::new();
    *MANAGED.get_or_init(|| std::env::args().any(|arg| arg == "--memodesk"))
}

#[derive(Clone, Debug, Deserialize, Serialize)]
#[serde(rename_all = "camelCase")]
pub(crate) struct Memo {
    id: String,
    version: u32,
    text: String,
    done_on: Option<String>,
    due: Option<String>,
    pub(crate) due_at: Option<i64>,
    plan_id: Option<String>,
    plan_version: Option<u32>,
}

impl Memo {
    fn into_todo(self) -> TodoItem {
        TodoItem {
            text: self.text.clone(),
            done_on: self.done_on.clone(),
            due: self.due.clone(),
            reminded: true,
            memo: Some(self),
            ..TodoItem::default()
        }
    }
}

enum Reply {
    Snapshot(Value),
    Saved(Value, Option<String>),
}

pub(crate) struct Bridge {
    sender: Sender<(Value, Option<String>)>,
    receiver: Receiver<Reply>,
    next_poll: Instant,
    outstanding: bool,
    writes: usize,
    since: i64,
    reset_checked_at: i64,
    announced_resets: HashSet<(String, String, i64)>,
    rows: Option<Vec<Memo>>,
    host: Option<(String, i32, i32, i32, i32, bool)>,
    host_sent: Instant,
}

impl Bridge {
    pub(crate) fn new(ctx: &egui::Context) -> Self {
        let (sender, pending) = mpsc::channel::<(Value, Option<String>)>();
        let (finished, receiver) = mpsc::channel();
        let ctx = ctx.clone();
        std::thread::spawn(move || {
            let mut input = io::stdin().lock();
            let mut output = io::stdout().lock();
            while let Ok((request, capture)) = pending.recv() {
                let result = (|| -> io::Result<Value> {
                    writeln!(output, "{request}")?;
                    output.flush()?;
                    let mut line = String::new();
                    if input.read_line(&mut line)? == 0 {
                        return Err(io::ErrorKind::UnexpectedEof.into());
                    }
                    serde_json::from_str(&line).map_err(io::Error::other)
                })();
                let Ok(result) = result else {
                    ctx.send_viewport_cmd(egui::ViewportCommand::Close);
                    break;
                };
                let reply = if request["op"] == "list" { Reply::Snapshot(result) }
                    else { Reply::Saved(result, capture) };
                if finished.send(reply).is_err() { break; }
                ctx.request_repaint();
            }
        });
        Self {
            sender, receiver, next_poll: Instant::now(), outstanding: false, writes: 0, since: 0,
            reset_checked_at: chrono::Utc::now().timestamp(), announced_resets: HashSet::new(),
            rows: None,
            host: None,
            host_sent: Instant::now(),
        }
    }

    fn send(&mut self, request: Value, capture: Option<String>) {
        if self.sender.send((request, capture)).is_ok() {
            self.writes += 1;
        }
    }
}

impl App {
    pub fn poll_memodesk(&mut self, ctx: &egui::Context) {
        let Some(mut bridge) = self.memo_bridge.take() else { return };
        while let Ok(reply) = bridge.receiver.try_recv() {
            match reply {
                Reply::Snapshot(data) => {
                    bridge.outstanding = false;
                    if let Some(updated) = data["updated"].as_i64() {
                        bridge.since = updated;
                    }
                    if let Ok(notices) = serde_json::from_value::<Vec<Memo>>(data["notices"].clone()) {
                        for memo in notices {
                            self.alert("备忘到时间了", memo.text, TODO_PAGE, false);
                        }
                    }
                    if let Ok(rows) = serde_json::from_value::<Vec<Memo>>(data["items"].clone()) {
                        bridge.rows = Some(rows);
                    }
                }
                Reply::Saved(data, capture) => {
                    bridge.writes = bridge.writes.saturating_sub(1);
                    bridge.next_poll = Instant::now();
                    if let Some(error) = data["error"].as_str() {
                        self.say(error);
                        if let Some(text) = capture {
                            if !self.is_editing() && self.page == TODO_PAGE {
                                self.adding = true;
                                self.buf = text;
                                self.todo_input_focused = false;
                            }
                        }
                    } else if capture.is_some() {
                        self.memo_capture = None;
                        if let Ok(row) = serde_json::from_value::<Memo>(data["item"].clone()) {
                            let needs_reminder = row.due_at.is_none();
                            self.todos.items.retain(|item| item.memo.as_ref().is_none_or(|memo| memo.id != row.id));
                            self.todos.items.insert(0, row.into_todo());
                            self.todo_first = 0;
                            self.todo_pop.vel += 6.0;
                            if needs_reminder && self.page == TODO_PAGE && !self.is_editing() {
                                self.open_task_reminder(0);
                            }
                        }
                    }
                }
            }
        }
        if bridge.writes == 0 && !self.is_editing() {
            if let Some(rows) = bridge.rows.take() {
                self.memo_calendar = rows.iter().filter_map(|row| {
                    let start = row.due_at?;
                    let time = chrono::DateTime::from_timestamp(start, 0)?.with_timezone(&chrono::Local);
                    Some(crate::mail::Meeting {
                        uid: format!("memodesk:{}", row.id), title: row.text.clone(), start,
                        end: None, all_day: false, at: time.format("%m-%d %H:%M:%S").to_string(),
                    })
                }).collect();
                self.todos.items = rows.into_iter().map(Memo::into_todo).collect();
            }
        }
        if !bridge.outstanding && Instant::now() >= bridge.next_poll {
            self.memo_quota_tick(&mut bridge);
            bridge.outstanding = bridge.sender.send((json!({"op":"list","since":bridge.since}), None)).is_ok();
            bridge.next_poll = Instant::now() + Duration::from_secs(1);
        }
        ctx.request_repaint_after(Duration::from_secs(1));
        self.memo_bridge = Some(bridge);
    }

    fn memo_quota_tick(&mut self, bridge: &mut Bridge) {
        let now = chrono::Utc::now().timestamp();
        let previous = std::mem::replace(&mut bridge.reset_checked_at, now);
        if !self.module_on(QUOTA_PAGE) { return; }
        bridge.announced_resets.retain(|(_, _, at)| *at > now - 86400);
        let resets: Vec<_> = self.quota.config.accounts.iter()
            .filter(|account| account.enabled && account.provider == crate::quota::Provider::Codex)
            .filter_map(|account| self.quota.rows.get(&account.id).and_then(|r| r.as_ref().ok()).map(|row| (account, row)))
            .flat_map(|(account, row)| row.windows.iter().chain(&row.extra).filter_map(move |window| {
                let at = window.reset?;
                (at > previous && at <= now).then(|| (account.id.clone(), window.label.clone(), at))
            }))
            .collect();
        for reset in resets {
            let label = reset.1.clone();
            if bridge.announced_resets.insert(reset) {
                self.alert("Codex 额度重置", format!("{label}额度已到重置时间"), QUOTA_PAGE, false);
            }
        }
    }

    pub(crate) fn edit_memo_from_calendar(&mut self, uid: &str) -> bool {
        let Some(id) = uid.strip_prefix("memodesk:") else { return false };
        if let Some(index) = self.todos.items.iter().position(|item| item.memo.as_ref().is_some_and(|memo| memo.id == id)) {
            self.open_task_dialog(index);
        }
        true
    }

    pub(crate) fn memo_action(&mut self, op: &str, index: usize, text: Option<&str>, due: Option<Option<&str>>) -> bool {
        let (Some(bridge), Some(memo)) = (self.memo_bridge.as_mut(), self.todos.items.get(index).and_then(|i| i.memo.as_ref())) else {
            return false;
        };
        bridge.send(json!({
            "op":op, "id":memo.id, "version":memo.version, "text":text,
            "setDue":due.is_some(), "due":due.flatten(), "planId":memo.plan_id, "planVersion":memo.plan_version,
        }), None);
        true
    }

    pub(crate) fn memo_open(&mut self, op: &str) {
        #[cfg(windows)]
        if let Some(pid) = std::env::var("MEMODESK_PARENT_PID").ok().and_then(|value| value.parse::<u32>().ok()) {
            let _ = unsafe { windows::Win32::UI::WindowsAndMessaging::AllowSetForegroundWindow(pid) };
        }
        if matches!(op, "workbench" | "settings" | "vault") {
            self.settings_tab = match op { "vault" => 14, "settings" => 17, _ => 13 };
            self.open_settings();
            return;
        }
        if let Some(bridge) = &mut self.memo_bridge { bridge.send(json!({"op":op}), None); }
    }

    pub(crate) fn memo_host_ui(&mut self, ui: &mut egui::Ui) {
        let page = match self.settings_tab {
            13 => "memos", 14 => "vault", 15 => "worklogs", 16 => "reminders", 17 => "settings",
            _ => return,
        };
        let rect = ui.max_rect();
        let scale = ui.ctx().pixels_per_point();
        let host = (page.to_string(), (rect.min.x * scale).round() as i32,
            (rect.min.y * scale).round() as i32, (rect.width() * scale).round() as i32,
            (rect.height() * scale).round() as i32, ui.visuals().dark_mode);
        if let Some(bridge) = &mut self.memo_bridge {
            if bridge.host.as_ref() != Some(&host) || bridge.host_sent.elapsed() >= Duration::from_secs(1) {
                bridge.send(json!({"op":"host","page":host.0,"x":host.1,"y":host.2,"width":host.3,"height":host.4,"dark":host.5}), None);
                bridge.host = Some(host);
                bridge.host_sent = Instant::now();
            }
        }
    }

    pub(crate) fn memo_hide_host(&mut self) {
        if let Some(bridge) = &mut self.memo_bridge {
            if bridge.host.take().is_some() { bridge.send(json!({"op":"hide-host"}), None); }
        }
    }

    pub fn restart_memodesk_island(&mut self) {
        self.save_window_before_exit();
        self.memo_open("restart");
    }

    pub(crate) fn memo_add(&mut self, text: &str) {
        let Some(bridge) = &mut self.memo_bridge else { return };
        #[cfg(windows)]
        if self.memo_capture.as_ref().is_none_or(|(prior, _)| prior != text) {
            match unsafe { windows::Win32::System::Com::CoCreateGuid() } {
                Ok(id) => self.memo_capture = Some((text.to_string(), format!("{:032x}", id.to_u128()))),
                Err(_) => return,
            }
        }
        if let Some((_, id)) = &self.memo_capture {
            bridge.send(json!({"op":"add","id":id,"text":text}), Some(text.to_string()));
        }
    }
}
