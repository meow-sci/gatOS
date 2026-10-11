#!/usr/bin/env python3
"""Select old rolling mod releases; never select guest or historical releases."""
import argparse
import json
import re
import sys

# All named releases at the distribution migration (GitHub audit 2026-10-11).
# v1.1.0 also matches the new run-number format, so regex filtering alone is unsafe.
PROTECTED_TAGS = frozenset({'v1.0.0', 'v1.0.1', 'v1.1.0'})


def matches_channel(release, channel):
    tag = release['tag_name']
    if tag in PROTECTED_TAGS:
        return False
    suffix = '' if channel == 'main' else '-beta'
    return (bool(release.get('prerelease')) == (channel == 'feature')
            and re.fullmatch(r'v1\.[1-9][0-9]*\.0' + suffix, tag) is not None)


def stale_tags(pages, channel, keep):
    if channel not in ('main', 'feature') or keep < 1:
        raise ValueError('Channel must be main or feature and retention must be positive')
    releases = [release for page in pages for release in page
                if not release.get('draft') and matches_channel(release, channel)]
    releases.sort(key=lambda release: (release['published_at'], release['id']), reverse=True)
    return [release['tag_name'] for release in releases[keep:]]


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--channel', choices=['main', 'feature'], required=True)
    parser.add_argument('--keep', type=int, required=True)
    args = parser.parse_args()
    for tag in stale_tags(json.load(sys.stdin), args.channel, args.keep):
        print(tag)
