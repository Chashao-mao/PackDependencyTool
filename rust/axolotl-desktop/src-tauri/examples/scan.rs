fn main() {
    let root = std::env::args().nth(1).expect("mods root");
    let conflicts = axolotl_desktop_lib::conflicts::scan(std::path::Path::new(&root));
    println!("{} conflicts", conflicts.len());
    for conflict in conflicts.iter().take(20) {
        println!("[{}] {} -> {}", conflict.kind, conflict.title, conflict.mods.join(", "));
        println!("  {}", conflict.detail);
    }
}
