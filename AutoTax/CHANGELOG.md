# Changelog

## 1.0.0 — unreleased development version

- Controls now live inside the keep's tax parchment: taxpayer count removed; this month's tax and the tax level line up with the slider; a status line (what Auto Tax is doing, popularity, number of adjustments; hover for the reason), a wax-seal on/off toggle with accumulated extra earnings, and the target band with small up/down arrows on the right.
- Automatic tax changes animate the slider handle (glide, fading ghost and trail, gold glow); while the keep panel is open they go through the game's own tax action so the slider and texts update.
- Extra earnings replace the cumulative tax total: gold gained compared with leaving the tax at the level the player had when Auto Tax took over. Saves keep it with the baseline and adjustment count; older saves start from zero.

- Dynamic popularity-based tax selection; provisional adjustable targets 99–99.5.
- Recalculate recovery and harvest taxes from actual bonuses, with balanced-tax handling and cap anticipation.
- Never select donations automatically; preserve manual donations until taxation or no tax is selected.
- Remove the initial full-month observation policy and optional automatic donations.
- Correct food and tax preview contributions before evaluating the policy.
- In-game tax panel controls and per-save settings, independent of new-game defaults.
- Chinese and English settings; enabled by default; single-player guards.
- Offline validation only; in-game acceptance deferred. Earlier candidate ZIP is obsolete.
