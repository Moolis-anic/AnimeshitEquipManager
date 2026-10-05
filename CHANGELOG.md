# Changelog

All notable changes to this project are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] - 2026-10-05

### Added

- Single multi-select config group (`Equipment Slot` / `装备插槽`): one row per slot with
  `Player` / `AI` / `Teammate` checkboxes. A ticked audience means the slot is hidden for that
  audience, so ticking all three hides it for everyone.
- The Teammate checkboxes are always present, whether PitFireTeam is installed or not.
- Preview models are covered as well: inventory paper-doll, Hideout character, other-player profile
  screens (including the PitFireTeam teammate profile), side selection and squad previews.
- PitFireTeam teammate detection: the follower list during a raid, and teammate profile ownership in
  the menu, so a teammate is recognised by the equipment it renders.
- Gear context menu entries to toggle each audience for the equipped item.
- Automatic config migration: values from the old per-audience groups and from the legacy
  `[Slot Visibility]` section are merged into the new layout, and the stale sections are removed.
- English and Chinese localization (`lang/en.json`, `lang/chs.json`).

### Changed

- Configuration layout: three per-audience groups with 24 boolean entries became a single group with
  eight multi-select entries.
- Hidden state is re-asserted continuously (re-checked within 100 ms), so a model rebuild or re-sync
  can no longer restore renderers that have to stay hidden.
- Logging is quiet by default: one config summary line at startup, one line per
  (slot, audience, equipment), and a single "resolved" transition line per preview. Diagnostic
  fields and unrelated previews are no longer logged.

### Fixed

- Hideout and inventory paper-doll previews apply the saved settings without entering a raid first.
- The teammate profile window and the side selection / squad previews follow the Teammate checkboxes.
- Renaming the config group after a game language change no longer loses values or creates
  duplicate sections.

### Known issues

- An avatar in the PitFireTeam teammate list can still show slots that should be hidden. That screen
  does not produce any plugin log, so the case is parked until it can be traced.

[1.1.0]: ../../releases/tag/v1.1.0
