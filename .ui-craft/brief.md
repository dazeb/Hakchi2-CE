# Hakchi Linux desktop

Build a compact native Linux frontend for the existing Hakchi CLI. The AppImage
opens a real window with no arguments; explicit command arguments retain CLI behavior.
The user selected a compact dark SNES interface.

The primary task is preparing a local ROM library and sending it to a console
already running compatible hakchi firmware. Use a dominant searchable game list,
game-art/detail preview, connection settings, visible activity output and a clear
sync action. Import and browsing must work without a console. Errors must retain
the backend's useful text; operations must run off the UI thread.

Use Avalonia's native controls and Fluent dark theme. Reuse the existing SNES controller
art and a muted purple accent, with Super Famicom button colors. No decorative animation or mock game catalogue.
Signature detail: the selected game's art stays visible beside the library.

Persist the user's library and connection settings outside the AppImage using XDG
directories. Do not store passwords. SSH uses existing keys/agent and known hosts.
Show confirmation for console writes. Keep raw FEL flashing in the documented CLI.
The maintainer confirms the SNES setup, emulator installation, sync and game launch
workflow on their console. Automated tests use fixtures; do not claim Windows GUI
feature parity or universal hardware compatibility.
