# DualAudio UI design reference (minimal, website-matched)

These four HTML files are design MOCKUPS (made on a design canvas), not app code. Use them only as the
visual reference for the WPF windows: layout, spacing, sizes, colours, copy. Ignore the `<x-dc>`,
`<sc-if>`, `<sc-for>`, `{{…}}` and `<script type="text/x-dc">` mockup machinery; the `renderVals()`
function in each file shows the text and colours used for each state.

| File | Window | Size (px) |
|------|--------|-----------|
| MinDashboard.html | Main window (dashboard) — states: receiving, connected, reconnecting, notfound, bluetoothoff | 960 x 640 |
| MinFlyout.html | Tray flyout | 360 x 460 |
| MinOnboarding.html | Onboarding steps 3 (pick phone) and 4 (play something) | 640 x 520 |
| MinSettings.html | Settings window (showConflict = hotkey-in-use warning) | 960 x 640 |

## Tokens (same as the website, dualaudio-site/styles.css)

| Token | Value | Use |
|-------|-------|-----|
| Background | #0b0d10 | Window background |
| Raised | #14171c | Cards, flyout |
| Raised 2 | #1b1f26 | Inner tiles, secondary buttons, empty meter segments |
| Border | #2a2f38 | 1 px card/button borders, dividers, slider track |
| Text | #e8eaed | Primary text |
| Text muted | #a9b0bb | Secondary text, labels |
| Accent | #7c9cff | Primary buttons, slider fill/thumb, status dot, meter, links |
| On accent | #0b0d10 | Text on accent buttons |
| Warning | #f5c062 | Bluetooth off, hotkey conflict |

- Radius: 12 px cards, 10 px buttons/inputs, 999 px pills. Spacing: 24 px window padding, 16 px between cards, 20 px card padding.
- Type: Segoe UI (system). Titles 18-24 px bold, card headings 15 px semibold, body 14 px, small 12-13 px.
- NO effects: no DropShadowEffect, BlurEffect, glows, gradients, acrylic/mica, or animations (runs next to games).

Icons: branding/DualAudio.ico (normal), branding/DualAudio-muted.ico (tray icon while phone audio is muted).
