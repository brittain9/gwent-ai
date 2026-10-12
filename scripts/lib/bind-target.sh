# Sourced by scripts/run-local.sh (in the sandbox launcher): where bwrap binds
# a file so that reading a symlinked path such as /etc/hosts finds it.
# bwrap refuses to mount on a symlink, so the file goes where the links lead.
# BIND_TARGET_ROOT prefixes every filesystem read (tests); the output stays
# relative to that root.

# bind_target PATH [HIDDEN_DIR]: prints the destination for a bind over PATH.
# Without HIDDEN_DIR that is the end of the symlink chain (readlink -f).
# With HIDDEN_DIR, which the sandbox covers with an empty tmpfs (the host's
# root at /run/host in a Toolbox or Distrobox container), the first link of
# the chain that lands inside it wins: that path is gone after the tmpfs, so
# the chain ends at the file bound there. A chain that never enters HIDDEN_DIR
# ends where it ends.
bind_target() {
    local path="$1" hidden="${2:-}" root="${BIND_TARGET_ROOT:-}" hop="$1" link parent
    if [[ -n "$hidden" ]]; then
        for _ in {1..40}; do  # the kernel gives up after 40 links in one lookup
            link="$(readlink -- "$root$hop")" || break  # not a link: the chain ended
            # A relative link starts in the real directory of the link itself.
            parent="$(readlink -f -- "$root$(dirname -- "$hop")")"
            parent="${parent#"$root"}"
            [[ "$link" == /* ]] || link="${parent%/}/$link"
            # Without resolving links: the next hop may not exist after the tmpfs.
            hop="$(realpath -sm -- "$root$link")"
            hop="${hop#"$root"}"
            if [[ "$hop" == "${hidden%/}"/* ]]; then
                printf '%s\n' "$hop"
                return
            fi
        done
    fi
    path="$(readlink -f -- "$root$path")"
    printf '%s\n' "${path#"$root"}"
}
