Pennyworth for Linux (x64)

Run it:        bin/pennyworth
Add a launcher (optional, per user):
    cp share/applications/pennyworth.desktop ~/.local/share/applications/
    cp -r share/icons/hicolor ~/.local/share/icons/
    then edit Exec= in the copy to the full path of bin/pennyworth

Nothing needs the .NET SDK, and the app itself needs no administrator rights: it runs as you and installs nothing.

The system supplies:
    required      glibc 2.27 or newer, libstdc++, libicu, libfontconfig, and an X11 or Wayland session
    optional      libsecret-1 (the desktop keyring, for API keys; without it only the keyring is turned off)
                  libssl (HTTPS, for the cloud assistant)
                  polkit (pkexec) and an authentication agent, for administrator access (below)

Administrator access (optional, Linux only)
    Pennyworth never asks for a password itself and never sees it. Four things need administrator rights, and it asks the
    system for them (polkit, the same prompt as any other program) only when YOU press a button that needs them:
        - memory modules: reads the memory information from the computer's firmware (read-only)
        - full system space: measures snapshots and file system metadata (read-only)
        - folders you cannot open (/root and the like): reads only their sizes, so Storage adds up (read-only)
        - cleaning system package caches / crash reports: deletes ONLY the files listed in the preview you confirmed
    One password prompt turns administrator access on for the whole session: after that every one of the four works
    without asking again. It turns off when you press "Lock", after 15 minutes without a request, or when Pennyworth closes.
    The header of the window shows whether it is Off, Active or needs a one-time setup.

    It is served by a small helper, pennyworth-helper, that runs as root only while access is on. It does exactly those
    four things (a fixed list; it reads no other file, runs no command, takes no path to delete) and talks only to the
    Pennyworth that started it, over a pipe: it listens on no socket or file.

    Set up (once): Pennyworth offers "Set up administrator access" in that header. It shows what it will install, the two
    files this tarball ships, both owned by root afterwards:
        /usr/libexec/pennyworth/pennyworth-helper              the helper
        /usr/share/polkit-1/actions/app.pennyworth.policy      the wording of the prompt
    and asks for the password once to copy them (the SHA-256 of each file is shown first and checked on the root-owned
    copy). You can do the same by hand: sudo ./install-helper.sh
    Pennyworth runs the helper only from that path, and only if the file and every folder above it are owned by root, not
    writable by group or others and not symbolic links. The AppImage does not contain the helper; one installed from this
    tarball is used by any build of the app. Nothing installs it without you pressing that button.

    Without the helper the first two buttons still work as one-off prompts (the system's own tools run through pkexec,
    asked again every time); locked folders and cleaning root-owned folders need the setup and say so.

    To remove it:  sudo rm /usr/libexec/pennyworth/pennyworth-helper /usr/share/polkit-1/actions/app.pennyworth.policy
                   (or sudo ./install-helper.sh --uninstall)

PENNYWORTH_DISPLAY=x11 keeps X11 (XWayland) inside a Wayland session.

Licence: LICENSE. Fonts (Golos Text, JetBrains Mono): SIL Open Font License 1.1, built into the app.
