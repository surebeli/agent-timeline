#!/usr/bin/env python3
"""新手引导图标注器：把拍好的面板底图 + 聚光标注合成一张 README 用图。

    annotate.py <spec.json> <输出目录>

spec 里每张图给一个或多个底图（`shots`）、画布、每个底图上的聚光标注点、
可选连接箭头，以及右侧图例文字。渲染走 headless Chrome：CSS 画圈/连线/排版
比手搓 CoreGraphics 省事，且中英两套只是换 spec 里的文案，版式完全一致。

坐标一律用**底图的 pt**（= 像素 / 2），与 window-tool 量出来的值同一口径，
免得在两个坐标系之间来回换算。聚光点编号跨所有 shots 连续累加（1,2,3…），
legend 顺序与编号一一对应。
"""
import base64
import json
import os
import subprocess
import sys

CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"

CSS = """
* { margin:0; padding:0; box-sizing:border-box; }
body { width:%(W)dpx; height:%(H)dpx; position:relative;
  background:
    radial-gradient(120%% 80%% at 12%% 100%%, #232838 0%%, transparent 55%%),
    radial-gradient(100%% 70%% at 100%% 0%%, #1d2436 0%%, transparent 50%%),
    #101014;
  font-family:"PingFang SC","SF Pro Text",-apple-system,sans-serif; color:#F2F3F5;
  -webkit-font-smoothing:antialiased; overflow:hidden; }
.title { position:absolute; left:%(PAD)dpx; top:%(TITLE_TOP)dpx;
  font-size:%(TITLE_SIZE)dpx; font-weight:700; letter-spacing:-.01em; }
.sub { position:absolute; left:%(PAD)dpx; top:%(SUB_TOP)dpx; width:%(SUB_W)dpx;
  font-size:%(SUB_SIZE)dpx; color:#A9AEB8; line-height:1.5; }
.shot { position:absolute; border-radius:12px; box-shadow:0 26px 60px rgba(0,0,0,.55); }
/* 聚光圈：不遮内容，只把注意力圈过去 */
.spot { position:absolute; border-radius:999px; border:3px solid #F0A050;
  box-shadow:0 0 0 6px rgba(240,160,80,.18), 0 0 22px rgba(240,160,80,.45); }
.badge { position:absolute; width:30px; height:30px; border-radius:999px;
  background:#F0A050; color:#101014; font-size:17px; font-weight:700;
  display:flex; align-items:center; justify-content:center;
  box-shadow:0 3px 10px rgba(0,0,0,.5); }
.arrow-wrap { position:absolute; display:flex; align-items:center; gap:10px; }
.arrow-wrap .line { width:64px; height:3px; background:#F0A050; border-radius:2px; position:relative; }
.arrow-wrap .line::after { content:""; position:absolute; right:-2px; top:-6px;
  border:8px solid transparent; border-left-color:#F0A050; }
.arrow-wrap .label { font-size:24px; color:#F0A050; font-weight:600; white-space:nowrap; }
.legend { position:absolute; left:%(LEG_X)dpx; top:%(LEG_Y)dpx; width:%(LEG_W)dpx; }
.legend .row { display:flex; gap:14px; margin-bottom:%(LEG_GAP)dpx; align-items:flex-start; }
.legend .n { flex:none; width:26px; height:26px; border-radius:999px; background:#F0A050;
  color:#101014; font-size:15px; font-weight:700;
  display:flex; align-items:center; justify-content:center; margin-top:2px; }
.legend .t { font-size:%(LEG_SIZE)dpx; line-height:1.5; }
.legend .t b { color:#FFFFFF; font-weight:600; }
.legend .t span { color:#A9AEB8; }
footer { position:absolute; left:%(PAD)dpx; bottom:%(PAD)dpx;
  font-size:22px; color:#4E5969; }
"""


def render(item, out_dir):
    W, H = item["canvas"]
    style = CSS % {
        "W": W, "H": H, "PAD": item.get("pad", 72),
        "TITLE_TOP": item.get("titleTop", 64), "TITLE_SIZE": item.get("titleSize", 60),
        "SUB_TOP": item.get("subTop", 148), "SUB_SIZE": item.get("subSize", 30),
        "SUB_W": item.get("subWidth", W - 2 * item.get("pad", 72)),
        "LEG_X": item["legendAt"][0], "LEG_Y": item["legendAt"][1],
        "LEG_W": item.get("legendWidth", 560), "LEG_SIZE": item.get("legendSize", 26),
        "LEG_GAP": item.get("legendGap", 20),
    }

    parts = [f"<style>{style}</style>",
             f'<div class="title">{item["title"]}</div>',
             f'<div class="sub">{item["sub"]}</div>']

    all_spots = []  # (label, desc) 累积，用于 legend 编号
    counter = [0]

    for shot in item["shots"]:
        with open(shot["path"], "rb") as fh:
            b64 = base64.b64encode(fh.read()).decode()
        scale = shot.get("scale", 1.0)
        sx, sy = shot["at"]
        sw = shot["size"][0] * scale
        sh = shot["size"][1] * scale
        radius = shot.get("radius", 12)
        parts.append(
            f'<img class="shot" style="left:{sx}px;top:{sy}px;width:{sw:.0f}px;'
            f'height:{sh:.0f}px;border-radius:{radius}px" '
            f'src="data:image/png;base64,{b64}">')

        for sp in shot.get("spots", []):
            counter[0] += 1
            n = counter[0]
            cx = sx + sp["at"][0] * scale
            cy = sy + sp["at"][1] * scale
            r = sp.get("r", 17) * scale
            parts.append(
                f'<div class="spot" style="left:{cx-r:.0f}px;top:{cy-r:.0f}px;'
                f'width:{2*r:.0f}px;height:{2*r:.0f}px"></div>')
            bx, by = sp.get("badgeOffset", (r + 8, -r - 30))
            parts.append(
                f'<div class="badge" style="left:{cx+bx:.0f}px;top:{cy+by:.0f}px">{n}</div>')
            all_spots.append(sp)

    for arrow in item.get("arrows", []):
        ax, ay = arrow["at"]
        parts.append(
            f'<div class="arrow-wrap" style="left:{ax}px;top:{ay}px">'
            f'<div class="label">{arrow.get("label", "")}</div>'
            f'<div class="line"></div></div>')

    rows = "".join(
        f'<div class="row"><div class="n">{i}</div>'
        f'<div class="t"><b>{sp["label"]}</b> <span>{sp["desc"]}</span></div></div>'
        for i, sp in enumerate(all_spots, 1))
    parts.append(f'<div class="legend">{rows}</div>')
    parts.append(f'<footer>{item["footer"]}</footer>')

    html = os.path.join(out_dir, item["name"] + ".html")
    png = os.path.join(out_dir, item["name"] + ".png")
    with open(html, "w", encoding="utf-8") as fh:
        fh.write("<meta charset='utf-8'>" + "".join(parts))
    subprocess.run(
        [CHROME, "--headless", "--disable-gpu", "--hide-scrollbars",
         f"--screenshot={png}", f"--window-size={W},{H}",
         "--force-device-scale-factor=1", html],
        capture_output=True, check=True)
    print(f"  {W}x{H} → {os.path.basename(png)}")
    return png


def main():
    if len(sys.argv) != 3:
        raise SystemExit("用法: annotate.py <spec.json> <输出目录>")
    spec = json.load(open(sys.argv[1], encoding="utf-8"))
    out_dir = sys.argv[2]
    os.makedirs(out_dir, exist_ok=True)
    for item in spec["images"]:
        render(item, out_dir)


if __name__ == "__main__":
    main()
