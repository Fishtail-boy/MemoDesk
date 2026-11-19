param([Parameter(Mandatory)][string]$Source)

$ErrorActionPreference = 'Stop'
$Source = (Resolve-Path -LiteralPath $Source).Path
$utf8 = [Text.UTF8Encoding]::new($false)

function Change-Source([string]$File, [string]$Before, [string]$After) {
    $path = Join-Path $Source ('src/' + $File)
    $contents = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
    $Before = $Before.TrimEnd().Replace("`r`n", "`n")
    $After = $After.TrimEnd().Replace("`r`n", "`n")
    if (!$contents.Contains($Before)) { throw "Unexpected Daozai source in $File" }
    [IO.File]::WriteAllText($path, $contents.Replace($Before, $After), $utf8)
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'memodesk.rs') -Destination (Join-Path $Source 'src/memodesk.rs')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'memodesk_ui_tests.rs') -Destination (Join-Path $Source 'src/memodesk_ui_tests.rs')
Change-Source 'lib.rs' 'pub mod model;' "pub mod model;`npub mod memodesk;"
Change-Source 'ui_tests.rs' 'mod doubao;' @'
mod doubao;
#[path = "memodesk_ui_tests.rs"]
mod memodesk;
'@

Change-Source 'model.rs' 'pub struct TodoItem {' @'
pub struct TodoItem {
    pub(crate) memo: Option<crate::memodesk::Memo>,
'@
Change-Source 'model.rs' '                list.items.push(TodoItem {' @'
                list.items.push(TodoItem {
                    memo: None,
'@
Change-Source 'model.rs' @'
        let mut s = Self::default();
        for (k, v) in read_kv(&config_dir().join("settings.txt")) {
'@ @'
        let mut s = Self::default();
        if crate::memodesk::managed() {
            s.visible_pages = [false, false, false, false, true, false, true, true, false];
            s.page_order = [4, 7, 6, 0, 1, 2, 3, 5, 8];
            s.wechat_notifications = true;
            s.clipboard_history = false;
        }
        for (k, v) in read_kv(&config_dir().join("settings.txt")) {
'@

Change-Source 'app.rs' '    pub(crate) todos: model::TodoList,' @'
    pub(crate) todos: model::TodoList,
    pub(crate) memo_bridge: Option<crate::memodesk::Bridge>,
    pub(crate) memo_capture: Option<(String, String)>,
    pub(crate) memo_calendar: Vec<crate::mail::Meeting>,
'@
Change-Source 'app.rs' '            todos: model::TodoList::load(),' @'
            todos: if crate::memodesk::managed() { model::TodoList::default() } else { model::TodoList::load() },
            memo_bridge: crate::memodesk::managed().then(|| crate::memodesk::Bridge::new(ctx)),
            memo_capture: None,
            memo_calendar: Vec::new(),
'@
Change-Source 'app.rs' '            4 => ("待办", "清单", theme::GREEN),' @'
            4 if self.memo_bridge.is_some() => ("备忘", "清单", theme::GREEN),
            4 => ("待办", "清单", theme::GREEN),
'@
Change-Source 'app.rs' '    dots: [Spring; 9],' '    dots: [Spring; PAGE_NAMES.len() + 2],'
Change-Source 'app.rs' '        if !self.settings.visible_pages[page] {' '        if !self.module_on(page) {'
Change-Source 'app.rs' '        if !self.settings.visible_pages[self.page] {' '        if !self.module_on(self.page) {'
Change-Source 'app.rs' 'if page == self.page || !self.settings.visible_pages[page]' 'if page == self.page || !self.module_on(page)'
Change-Source 'app.rs' 'pub fn module_on(&self,page:usize)->bool {self.settings.visible_pages.get(page).copied().unwrap_or(false)}' @'
pub fn module_on(&self, page: usize) -> bool {
        if page == crate::memodesk::ASSETS_PAGE || page == crate::memodesk::VAULT_PAGE { return self.memo_bridge.is_some(); }
        self.settings.visible_pages.get(page).copied().unwrap_or(false)
    }
'@
Change-Source 'app.rs' @'
    fn navigate_page(&mut self,next:bool) {
        let position=self.settings.page_order.iter().position(|page|*page==self.page).unwrap();
        for offset in 1..=PAGE_NAMES.len() {
            let page=self.settings.page_order[(position+if next {offset}else{PAGE_NAMES.len()-offset})%PAGE_NAMES.len()];
            if self.settings.visible_pages[page] {
                self.go(page,if next {1.0}else{-1.0});
                break;
            }
        }
    }
'@ @'
    fn visible_page_order(&self) -> Vec<usize> {
        let mut pages = Vec::with_capacity(self.dots.len());
        for page in self.settings.page_order {
            if self.module_on(page) { pages.push(page); }
            if page == TODO_PAGE && self.module_on(crate::memodesk::ASSETS_PAGE) {
                pages.push(crate::memodesk::ASSETS_PAGE);
                pages.push(crate::memodesk::VAULT_PAGE);
            }
        }
        pages
    }

    pub(crate) fn navigate_page(&mut self, next: bool) {
        let pages = self.visible_page_order();
        let Some(position) = pages.iter().position(|&page| page == self.page) else { return };
        let offset = if next { 1 } else { pages.len() - 1 };
        self.go(pages[(position + offset) % pages.len()], if next { 1.0 } else { -1.0 });
    }
'@
Change-Source 'app.rs' 'PAGE_SETTINGS_TABS[page]' 'crate::memodesk::settings_tab(page)'
Change-Source 'app.rs' 'PAGE_SETTINGS_TABS[self.page]' 'crate::memodesk::settings_tab(self.page)'
Change-Source 'app.rs' 'PAGE_NAMES[self.page]' 'crate::memodesk::page_name(self.page)'
Change-Source 'app.rs' 'PAGE_NAMES[i]' 'crate::memodesk::page_name(i)'
Change-Source 'app.rs' 'let pages: Vec<_>=self.settings.page_order.iter().copied().filter(|page|self.settings.visible_pages[*page]).collect();' 'let pages = self.visible_page_order();'
Change-Source 'app.rs' 'for i in pages {' 'for &i in &pages {'
Change-Source 'app.rs' 'let position=|page|self.settings.page_order.iter().position(|p|*p==page).unwrap();' 'let position=|page|pages.iter().position(|p|*p==page).unwrap();'
Change-Source 'app.rs' '            _ => ("岛仔", "设置", theme::WHITE),' @'
            crate::memodesk::ASSETS_PAGE => ("数字资产", "", theme::CYAN),
            crate::memodesk::VAULT_PAGE => ("密码库", "", theme::CYAN),
            _ => ("岛仔", "设置", theme::WHITE),
'@
Change-Source 'app.rs' '            CALENDAR_PAGE => self.page_calendar(c,env),' @'
            CALENDAR_PAGE => self.page_calendar(c,env),
            crate::memodesk::ASSETS_PAGE => self.page_assets(c),
            crate::memodesk::VAULT_PAGE => self.page_vault(c),
'@
Change-Source 'app.rs' '        if (!self.is_editing() || self.todo_choosing) && inside && self.open && self.reveal > 0.95 {' @'
        let swipe_enabled = inside && self.open && self.reveal > 0.95 && !self.is_editing()
            && self.position_drag.is_none();
        if self.memo_swipe(&ctx, self.content_origin(win), self.expanded_height(), swipe_enabled) {
            self.wheel_delta = 0.0;
        } else if (!self.is_editing() || self.todo_choosing) && inside && self.open && self.reveal > 0.95 {
'@
Change-Source 'app.rs' '            .hint_text(egui::RichText::new("要做点什么？回车保存")' @'
            .hint_text(egui::RichText::new(if self.memo_bridge.is_some() { "记一条备忘，回车保存" } else { "要做点什么？回车保存" })
'@
Change-Source 'app.rs' '            let mut added=false;' @'
            if self.memo_bridge.is_some() {
                if !text.is_empty() {
                    if let Some(index) = self.editing { self.memo_action("edit", index, Some(&text), None); }
                    else { self.memo_add(&text); }
                }
                self.cancel_edit();
                return;
            }
            let mut added=false;
'@

Change-Source 'pages.rs' '        let can_edit = !self.todos.items.is_empty()' @'
        if self.memo_bridge.is_some() {
            let (main, label) = capsule(c, 41.0, 31.0, 66.0, 17.0, "memo-workbench", theme::BTN, theme::BTN_HOVER);
            label.text(74.0, 43.0, "工作台", size::SMALL, W::Regular, theme::WHITE, H::Center);
            if main.clicked() { self.memo_open("workbench"); }
        }
        let can_edit = !self.todos.items.is_empty()
'@
# Keep the original metric below the added quick entries.
Change-Source 'pages.rs' '        let pop = self.todo_pop.pos.abs().min(0.4);' @'
        let metric = if self.memo_bridge.is_some() { 9.0 } else { 0.0 };
        let pop = self.todo_pop.pos.abs().min(0.4);
'@
Change-Source 'pages.rs' 'hc.text(42.0, 61.0, &format!("{done}")' 'hc.text(42.0, 61.0 + metric, &format!("{done}")'
Change-Source 'pages.rs' 'hc.text(42.0 + w, 61.0, &format!("/{total}")' 'hc.text(42.0 + w, 61.0 + metric, &format!("/{total}")'
Change-Source 'pages.rs' 'c.text(42.0, 78.0, "已完成"' 'c.text(42.0, 78.0 + metric, "已完成"'
Change-Source 'pages.rs' 'if ui.button("取消提醒").clicked(){self.todos.items[i].due=None;self.todos.save();ui.close_menu();}' @'
if ui.button("取消提醒").clicked(){
                        if !self.memo_action("edit", i, None, Some(None)) {
                            self.todos.items[i].due=None;self.todos.save();
                        }
                        ui.close_menu();
                    }
'@
Change-Source 'pages.rs' '                if delete {' @'
                if delete {
                    if self.memo_action("delete", i, None, None) { break; }
'@
Change-Source 'pages.rs' '                    self.todos.items[i].toggle(&env.today);' @'
                    if self.memo_action(if now { "reopen" } else { "complete" }, i, None, None) {
                        if !now {
                            self.todo_pop.vel += 5.0;
                            self.pet.react(crate::extras::PetState::Happy);
                        }
                        break;
                    }
                    self.todos.items[i].toggle(&env.today);
'@

Change-Source 'extras.rs' '            let item=&mut self.todos.items[d.index];' @'
            let edited_due = due.unwrap();
            if self.memo_bridge.is_some() {
                let original = self.todos.items[d.index].due.as_deref();
                let changed_due = (edited_due.as_deref() != original).then_some(edited_due.as_deref());
                self.memo_action("edit", d.index, Some(&d.text), changed_due);
                return;
            }
            let item=&mut self.todos.items[d.index];
'@
Change-Source 'extras.rs' 'item.due=due.unwrap();' 'item.due=edited_due;'
Change-Source 'extras.rs' '        self.task_repeat_chips(c,91.0,81.0,25.0,19.0,28.0);' '        if self.memo_bridge.is_none(){ self.task_repeat_chips(c,91.0,81.0,25.0,19.0,28.0); }'
Change-Source 'extras.rs' '        c.text(43.0,95.0,"重复",10.5,W::Regular,theme::GRAY,H::Left);' '        if self.memo_bridge.is_none() { c.text(43.0,95.0,"重复",10.5,W::Regular,theme::GRAY,H::Left); }'
Change-Source 'extras.rs' '        if setting_button(c,289.0,81.0,77.0,quick,false,"task-repeat-all"){' '        if self.memo_bridge.is_none() && setting_button(c,289.0,81.0,77.0,quick,false,"task-repeat-all"){'
Change-Source 'extras.rs' '        self.task_repeat_chips(c,41.0,84.0,17.0,16.0,19.5);' '        if self.memo_bridge.is_none(){ self.task_repeat_chips(c,41.0,84.0,17.0,16.0,19.5); }'
Change-Source 'extras.rs' '        "待办到时间了"=>crate::paint::checklist' '        "待办到时间了" | "备忘到时间了"=>crate::paint::checklist'
Change-Source 'extras.rs' '                                ui.vertical(|ui|self.updates.ui(ui));' @'
                                if self.memo_bridge.is_some() {
                                    if ui.button("MemoDesk 设置").clicked() { self.memo_open("settings"); }
                                } else { ui.vertical(|ui|self.updates.ui(ui)); }
'@
Change-Source 'extras.rs' '        if !self.settings_open{return;}' @'
        if !self.settings_open {
            self.memo_hide_host();
            return;
        }
'@
Change-Source 'extras.rs' '                for (name,start,end) in SETTINGS_GROUPS {' @'
                egui::ScrollArea::vertical().id_salt("settings-navigation").show(ui, |ui| {
                let managed = self.memo_bridge.is_some();
                for (name,start,end) in (if managed {
                    vec![("备忘与提醒",17,17), ("备忘",13,13), ("密码库",14,14), ("数字资产",18,18), ("工作记录",15,15), ("提醒记录",16,16)]
                } else { vec![] }).into_iter().chain(SETTINGS_GROUPS.into_iter().filter(|(_,start,_)| !managed || !matches!(*start, 4 | 9))) {
'@
Change-Source 'extras.rs' @'
                    ui.add_space(3.0);
                }
            });
            egui::CentralPanel::default()
'@ @'
                    ui.add_space(3.0);
                }
                });
            });
            egui::CentralPanel::default()
'@
Change-Source 'extras.rs' '                    if start==9 {ui.add_space((ui.available_height()-38.0).max(0.0));}' '                    if start==9 && self.memo_bridge.is_none() {ui.add_space((ui.available_height()-38.0).max(0.0));}'
Change-Source 'extras.rs' '                let titles=["常规","音乐与歌词","专注","健康提醒","待办","置物","通知","邮箱","日程","关于","页面管理","AI 额度","天气"];' @'
                let titles=["常规","音乐与歌词","专注","健康提醒","待办","置物","通知","邮箱","日程","关于","页面管理","AI 额度","天气","备忘","密码库","工作记录","提醒记录","备忘与提醒","数字资产"];
                if self.memo_bridge.is_some() && self.settings_tab == 9 { self.settings_tab = 0; }
                if self.memo_bridge.is_some() && self.settings_tab >= 13 {
                    self.memo_host_ui(ui);
                    return;
                }
                self.memo_hide_host();
'@
Change-Source 'extras.rs' 'titles[self.settings_tab.min(12)]' 'titles[self.settings_tab.min(18)]'
Change-Source 'extras.rs' '.with_inner_size([690.0,560.0]).with_min_inner_size([600.0,450.0])' '.with_inner_size(if self.memo_bridge.is_some() {[1060.0,680.0]}else{[690.0,560.0]}).with_min_inner_size([600.0,450.0])'
Change-Source 'extras.rs' '                            if a!=autostart{self.pending.push(Cmd::ToggleAutostart);}' '                            if a!=autostart && self.memo_bridge.is_none(){self.pending.push(Cmd::ToggleAutostart);}'

Change-Source 'calendar_ui.rs' '        let local=self.calendar.local.iter().map(|entry|(&entry.event,Source::Local));' @'
        let local=self.calendar.local.iter().map(|entry|(&entry.event,Source::Local))
            .chain(self.memo_calendar.iter().map(|event|(event,Source::Local)));
'@
Change-Source 'calendar_ui.rs' '        if let Some(uid)=edit_local{self.open_calendar_editor(Some(&uid));}' @'
        if let Some(uid)=edit_local {
            if !self.edit_memo_from_calendar(&uid) { self.open_calendar_editor(Some(&uid)); }
        }
'@

Change-Source 'main.rs' '        let tray = app.start_tray(daozai::tray::State {' '        let tray = (!daozai::memodesk::managed()).then(|| app.start_tray(daozai::tray::State {'
Change-Source 'main.rs' @'
            autostart,
        });
        diagnostics::startup("初始化通知监听");
'@ @'
            autostart,
        })).flatten();
        diagnostics::startup("初始化通知监听");
'@
Change-Source 'main.rs' '        if self.resource_at.ready(Duration::from_secs(30)){' @'
        self.app.poll_memodesk(ctx);
        if self.resource_at.ready(Duration::from_secs(30)){
'@
Change-Source 'main.rs' '                Cmd::RestartApp=>{' @'
                Cmd::RestartApp if daozai::memodesk::managed() => self.app.restart_memodesk_island(),
                Cmd::RestartApp=>{
'@
Change-Source 'main.rs' '    if !daozai::tray::single_instance() {' '    if !daozai::memodesk::managed() && !daozai::tray::single_instance() {'
Change-Source 'main.rs' '    std::thread::spawn(daozai::record_install_size);' '    if !daozai::memodesk::managed() { std::thread::spawn(daozai::record_install_size); }'
Change-Source 'diagnostics.rs' 'fn relaunch_after_failure(reason: &str) -> bool {' @'
fn relaunch_after_failure(reason: &str) -> bool {
    if daozai::memodesk::managed() { return false; }
'@

# Brand the embedded island while preserving its storage and IPC identifiers.
$assets = Join-Path $PSScriptRoot '../../src/MemoDesk.App/Assets'
Copy-Item -LiteralPath (Join-Path $assets 'memo.ico') -Destination (Join-Path $Source 'assets/daozai.ico') -Force
Copy-Item -LiteralPath (Join-Path $assets 'memo.png') -Destination (Join-Path $Source 'assets/daozai-logo.png') -Force
Copy-Item -Path (Join-Path $PSScriptRoot 'icons/*.svg') -Destination (Join-Path $Source 'assets/settings-icons/system') -Force
Change-Source 'extras.rs' '                        _=>egui::include_image!("../assets/settings-icons/system/about.svg"),' @'
                        13=>egui::include_image!("../assets/settings-icons/system/memo.svg"),
                        14=>egui::include_image!("../assets/settings-icons/system/vault.svg"),
                        15=>egui::include_image!("../assets/settings-icons/system/worklogs.svg"),
                        16=>egui::include_image!("../assets/settings-icons/system/reminders.svg"),
                        17=>egui::include_image!("../assets/settings-icons/system/settings.svg"),
                        _=>egui::include_image!("../assets/settings-icons/system/general.svg"),
'@
Change-Source 'extras.rs' "        self.updates.poll(ctx);`n        self.updates.dialog_ui(ctx);" "        if self.memo_bridge.is_none() { self.updates.poll(ctx); self.updates.dialog_ui(ctx); }"
Get-ChildItem -LiteralPath (Join-Path $Source 'src') -Filter '*.rs' -Recurse | ForEach-Object {
    $content = [IO.File]::ReadAllText($_.FullName).Replace('岛仔', '备忘')
    [IO.File]::WriteAllText($_.FullName, $content, $utf8)
}
$resource = Join-Path $Source 'app.rc'
$content = [IO.File]::ReadAllText($resource).Replace('岛仔 Daozai', '备忘').Replace('岛仔', '备忘').Replace('VALUE "InternalName", "Daozai"', 'VALUE "InternalName", "MemoDeskIsland"')
[IO.File]::WriteAllText($resource, $content, $utf8)
