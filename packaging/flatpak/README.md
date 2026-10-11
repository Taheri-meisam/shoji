# Why there is no Flatpak

Shoji is mostly a sensor reader, and a Flatpak sandbox hides exactly what it reads: `/sys/class/hwmon` and the
RAPL power counters, other processes in `/proc` (CPU per app, the app in focus for scenes), `nvidia-smi` and the
host's `wpctl` / `xprop` / `xdotool` tools. Opening all of that up (`--filesystem=host`, `--talk-name=org.freedesktop.Flatpak`
to run host commands) would leave a sandbox in name only, and Flathub wouldn't accept it.
So Shoji ships as a `.deb` (`scripts/build-deb.sh`) and as a tarball with `install.sh`, which run unsandboxed like any other system monitor.
