"""Generates the N.AIM Benchmark Assistant logo (SVG) - a white Nokia 3310 inside a house-shaped frame, stars and
water-reflection lines, in the style of the reference badge.  Run:  python make_logo.py   (writes the SVGs next to it)."""
import random, os
random.seed(33)
here = os.path.dirname(os.path.abspath(__file__))

def star(cx, cy, r, op=1):
    # four-point sparkle, same construction as the stars on the 3310.nz sites
    return (f'<path opacity="{op}" d="M{cx} {cy-r}Q{cx} {cy} {cx+r} {cy}Q{cx} {cy} {cx} {cy+r}'
            f'Q{cx} {cy} {cx-r} {cy}Q{cx} {cy} {cx} {cy-r}Z" fill="#fff"/>')

def phone(scale_small=False):
    # Nokia 3310: rounded body, earpiece, screen with a crosshair, navi key, 3x4 keypad (cut out with a mask)
    x0, y0, w, h = 190, 122, 132, 194
    cuts = []
    cuts.append('<rect x="236" y="133" width="40" height="5" rx="2.5"/>')                       # earpiece
    cuts.append('<rect x="206" y="148" width="100" height="56" rx="7"/>')                        # screen
    cuts.append('<rect x="233" y="214" width="46" height="19" rx="9.5"/>')                       # navi key
    for r in range(4):
        for c in range(3):
            cuts.append(f'<rect x="{210+c*35}" y="{243+r*17}" width="26" height="11" rx="5"/>')
    mask = ('<mask id="ph"><rect x="0" y="0" width="512" height="512" fill="#000"/>'
            f'<rect x="{x0}" y="{y0}" width="{w}" height="{h}" rx="28" fill="#fff"/>'
            f'<g fill="#000">{"".join(cuts)}</g></mask>')
    cross = ('<g fill="none" stroke="#fff" stroke-width="3.2" stroke-linecap="round">'
             '<circle cx="256" cy="176" r="13"/><path d="M256 154v10M256 188v10M234 176h10M268 176h10"/></g>'
             '<circle cx="256" cy="176" r="2.6" fill="#fff"/>')
    body = '<rect x="0" y="0" width="512" height="512" fill="#fff" mask="url(#ph)"/>'
    return mask, body + cross

def water():
    rows = []
    y = 332
    n = 0
    while y < 446:
        span_l = 96 + (n % 3) * 14 + n * 3
        span_r = 416 - ((n + 1) % 3) * 14 - n * 3
        x = span_l
        while x < span_r - 10:
            seg = random.choice([34, 52, 70, 96, 120])
            end = min(x + seg, span_r)
            rows.append(f'<rect x="{x}" y="{y}" width="{end-x}" height="6.5" rx="2" />')
            x = end + random.choice([8, 12, 16])
        y += 16.5
        n += 1
    return "".join(rows)

def logo_group():
    mask, ph = phone()
    stars = (star(158, 206, 13) + star(358, 174, 9, .9) + star(372, 236, 14) + star(334, 270, 8, .85)
             + star(150, 268, 10, .9) + star(232, 104, 7, .8))
    return f'''
  <defs>
    {mask}
    <linearGradient id="water" gradientUnits="userSpaceOnUse" x1="0" y1="330" x2="0" y2="450">
      <stop offset="0" stop-color="#ffffff"/><stop offset=".55" stop-color="#b9b0ff" stop-opacity=".8"/><stop offset="1" stop-color="#5a4bd6" stop-opacity=".35"/>
    </linearGradient>
    <filter id="glow" x="-20%" y="-20%" width="140%" height="140%"><feGaussianBlur stdDeviation="5" result="b"/><feMerge><feMergeNode in="b"/><feMergeNode in="SourceGraphic"/></feMerge></filter>
  </defs>
  <g filter="url(#glow)">
    <path d="M112 306V162L256 74L400 162V306" fill="none" stroke="#fff" stroke-width="17" stroke-linejoin="round"/>
    {ph}
    {stars}
    <g fill="url(#water)">{water()}</g>
  </g>'''

def svg(content, bg=None, size=512):
    bgs = ''
    if bg == 'icon':
        bgs = ('<defs><radialGradient id="bgr" cx="50%" cy="38%" r="75%"><stop offset="0" stop-color="#1b1b4a"/><stop offset=".6" stop-color="#0a0a1f"/><stop offset="1" stop-color="#04040c"/></radialGradient></defs>'
               '<rect width="512" height="512" rx="104" fill="url(#bgr)"/>'
               '<g fill="#ffe98a"><circle cx="60" cy="70" r="2.4"/><circle cx="452" cy="120" r="2"/><circle cx="470" cy="330" r="2.4"/><circle cx="38" cy="300" r="2"/><circle cx="420" cy="60" r="1.8"/><circle cx="90" cy="440" r="2"/></g>')
    return (f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="{size}" height="{size}">{bgs}{content}</svg>')

def small_icon():
    # simplified for 16-32 px: big white phone on a dark rounded square
    cuts = ['<rect x="164" y="132" width="184" height="104" rx="14"/>']
    for r in range(3):
        for c in range(3):
            cuts.append(f'<rect x="{168+c*62}" y="{262+r*50}" width="48" height="30" rx="10"/>')
    return ('<defs><mask id="p"><rect width="512" height="512" fill="#000"/><rect x="132" y="48" width="248" height="416" rx="54" fill="#fff"/>'
            f'<g fill="#000">{"".join(cuts)}</g></mask>'
            '<radialGradient id="bgr" cx="50%" cy="38%" r="75%"><stop offset="0" stop-color="#1b1b4a"/><stop offset="1" stop-color="#05050e"/></radialGradient></defs>'
            '<rect width="512" height="512" rx="104" fill="url(#bgr)"/>'
            '<rect width="512" height="512" fill="#fff" mask="url(#p)"/>'
            '<circle cx="256" cy="184" r="26" fill="none" stroke="#fff" stroke-width="12"/><circle cx="256" cy="184" r="7" fill="#fff"/>')

g = logo_group()
open(os.path.join(here, 'logo.svg'), 'w', encoding='utf-8').write(svg(g))
open(os.path.join(here, 'icon.svg'), 'w', encoding='utf-8').write(svg('<g transform="translate(256 262) scale(.9) translate(-256 -262)">' + g + '</g>', bg='icon'))
open(os.path.join(here, 'icon-small.svg'), 'w', encoding='utf-8').write(f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">{small_icon()}</svg>')
print('ok')
