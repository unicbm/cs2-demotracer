#![cfg_attr(windows, windows_subsystem = "windows")]

fn main() -> eframe::Result<()> {
    cs2_demotracer::gui::run_gui()
}
