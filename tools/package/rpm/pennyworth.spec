# Pennyworth RPM, built from the tarball that tools/package/build-linux.sh produces:
#   tools/package/rpm/build-rpm.sh <tarball> [outdir]
# It installs the app under /usr/lib/pennyworth and, root-owned, the administrator helper and its polkit policy, which is
# the one place the app trusts them from (/usr/libexec/pennyworth/pennyworth-helper). No %post script starts anything.
Name:           pennyworth
Version:        %{pw_version}
Release:        1
Summary:        See where your disk space went and free it safely
License:        MIT
URL:            https://github.com/Pennyworth-HQ/Pennyworth
BuildArch:      x86_64
AutoReqProv:    no
Requires:       libsecret, polkit, fontconfig, libicu
# The self-contained .NET tree and the AOT helper carry their own debug-free binaries.
%global debug_package %{nil}
%global __strip /bin/true
%global _build_id_links none

%description
Pennyworth explains your PC in plain language: where every byte of storage goes, what can be cleaned safely,
which apps use memory and the processor, and every part of the hardware. This package also installs the optional
administrator helper and its polkit policy, so "Administrator access" works without a separate setup step.

%prep

%build

%install
rm -rf %{buildroot}
mkdir -p %{buildroot}/usr/lib/pennyworth %{buildroot}/usr/bin %{buildroot}/usr/libexec/pennyworth \
         %{buildroot}/usr/share/polkit-1/actions %{buildroot}/usr/share/applications %{buildroot}/usr/share/icons
cp -a %{pw_tree}/lib/pennyworth/. %{buildroot}/usr/lib/pennyworth/
ln -s ../lib/pennyworth/Pennyworth %{buildroot}/usr/bin/pennyworth
install -m 755 %{pw_tree}/libexec/pennyworth/pennyworth-helper %{buildroot}/usr/libexec/pennyworth/pennyworth-helper
install -m 644 %{pw_tree}/share/polkit-1/actions/app.pennyworth.policy %{buildroot}/usr/share/polkit-1/actions/app.pennyworth.policy
install -m 644 %{pw_tree}/share/applications/pennyworth.desktop %{buildroot}/usr/share/applications/pennyworth.desktop
cp -a %{pw_tree}/share/icons/hicolor %{buildroot}/usr/share/icons/hicolor
mkdir -p %{buildroot}/usr/share/licenses/pennyworth
install -m 644 %{pw_tree}/LICENSE %{buildroot}/usr/share/licenses/pennyworth/LICENSE

%files
/usr/share/licenses/pennyworth/LICENSE
%dir /usr/lib/pennyworth
/usr/lib/pennyworth/*
/usr/bin/pennyworth
%dir /usr/libexec/pennyworth
%attr(0755,root,root) /usr/libexec/pennyworth/pennyworth-helper
%attr(0644,root,root) /usr/share/polkit-1/actions/app.pennyworth.policy
/usr/share/applications/pennyworth.desktop
/usr/share/icons/hicolor/*/apps/pennyworth.png
