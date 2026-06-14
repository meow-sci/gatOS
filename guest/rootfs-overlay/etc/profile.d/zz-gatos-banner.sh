# gatOS login banner — sourced by /etc/profile for interactive (login) shells,
# i.e. each SSH session a player opens through purrTTY. A non-interactive
# `ssh host cmd` runs a non-login shell and skips /etc/profile.d, so gatOS's own
# `echo ok` readiness/health probes never see this output.
#
# The banner content (logo + blank line + tagline) is baked at image-build time
# into /etc/gatos/banner from guest/gatos-banner + guest/gatos-tagline.
# The zz- prefix sorts it last so it prints right before the prompt.
[ -t 1 ] && [ -r /etc/gatos/banner ] && cat /etc/gatos/banner
