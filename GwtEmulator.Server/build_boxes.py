#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build_boxes.py — строит ObstacleBox[] для карты из MapData_N.asset + мешей коллизии.

Вход:
  * MapData_N.asset (Unity YAML): RegionDatas -> ModelGroups -> {Name:"kind_modelId_имя", Transes[]}
  * collision_shapes_by_id.json : modelId -> {center,size} локального AABB меша коллизии
    (сгенерирован extract-скриптом из GLB-мешей коллизии, имя файла = "{modelId}_имя_pz.glb")

Логика: для каждой группы берём modelId из Name (parts[1]); если для него есть меш
коллизии — трансформируем его AABB каждым Trans (TRS, Эйлер ZXY) в мировой прямоугольник
X/Z. Модели без меша коллизии (деревья, кусты, декор) пропускаются — это правильно.

  python build_boxes.py MapData_22.asset --net-id 2 --name Закавказье

ВАЖНО про соответствие: индекс MapData_N — это НЕ сетевой MapId. Свяжи их через террейн
(сетевая карта -> террейн по Y игрока -> MapData на том же террейне) или проверкой
GWT_FORCE_MAP. --net-id — это то, под каким ключом карта лежит в RoomServer.
"""
import re, json, math, argparse
import numpy as np

def euler_zxy(rx, ry, rz):
    rx, ry, rz = map(math.radians, (rx, ry, rz))
    cx, sx = math.cos(rx), math.sin(rx)
    cy, sy = math.cos(ry), math.sin(ry)
    cz, sz = math.cos(rz), math.sin(rz)
    Rx = np.array([[1,0,0],[0,cx,-sx],[0,sx,cx]])
    Ry = np.array([[cy,0,sy],[0,1,0],[-sy,0,cy]])
    Rz = np.array([[cz,-sz,0],[sz,cz,0],[0,0,1]])
    return Ry @ Rx @ Rz  # Unity Euler order ZXY

def world_box_xz(center, half, pos, rot, scale):
    R = euler_zxy(*rot)
    pts = []
    for sxn in (-1, 1):
        for syn in (-1, 1):
            for szn in (-1, 1):
                p = np.array([center[0]+sxn*half[0], center[1]+syn*half[1], center[2]+szn*half[2]]) * np.array(scale)
                pts.append(R @ p + np.array(pos))
    c = np.array(pts)
    return c[:,0].min(), c[:,2].min(), c[:,0].max(), c[:,2].max()

def parse_blocks(path):
    """Возвращает [(modelId, name, [(pos,rot,scale)...])] по каждой ModelGroup."""
    txt = open(path, encoding='utf-8', errors='replace').read()
    parts = re.split(r'\n\s*- Name:\s*', '\n' + txt)
    pos = re.compile(r'Pos:\s*\{x:\s*(-?[\d.eE+]+),\s*y:\s*(-?[\d.eE+]+),\s*z:\s*(-?[\d.eE+]+)\}')
    rot = re.compile(r'Rot:\s*\{x:\s*(-?[\d.eE+]+),\s*y:\s*(-?[\d.eE+]+),\s*z:\s*(-?[\d.eE+]+)\}')
    sca = re.compile(r'Scale:\s*\{x:\s*(-?[\d.eE+]+),\s*y:\s*(-?[\d.eE+]+),\s*z:\s*(-?[\d.eE+]+)\}')
    out = []
    for blk in parts[1:]:
        name = blk.splitlines()[0].strip()
        P, R, S = pos.findall(blk), rot.findall(blk), sca.findall(blk)
        t = name.split('_')
        mid = int(t[1]) if len(t) >= 2 and t[1].lstrip('-').isdigit() else None
        trs = [(tuple(map(float, P[i])), tuple(map(float, R[i])), tuple(map(float, S[i])))
               for i in range(min(len(P), len(R), len(S)))]
        out.append((mid, name, trs))
    return out

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('mapdata')
    ap.add_argument('--net-id', type=int, required=True)
    ap.add_argument('--name', default='')
    ap.add_argument('--collision', default='collision_shapes_by_id.json')
    ap.add_argument('--min-size', type=float, default=2.0, help='пропускать боксы мельче N юнитов')
    ap.add_argument('--pad', type=float, default=0.5, help='расширить каждый бокс на N (запас)')
    ap.add_argument('--size-templates', action='store_true',
                    help='ПРИБЛИЗИТЕЛЬНЫЕ боксы по типовому размеру (из имеющихся мешей) '
                         'для моделей без своего меша коллизии — позиция точная, размер средний')
    a = ap.parse_args()

    coll = {int(k): v for k, v in json.load(open(a.collision)).items()}
    groups = parse_blocks(a.mapdata)

    # Optional: learn a median box size per model-name word from the meshes we DO
    # have, and use it as an APPROXIMATE fallback for building instances whose exact
    # collision mesh isn't available (e.g. Moscow's 17xxx models aren't in the shared
    # mesh set). Only words with >=5 samples are trusted. Boxes placed this way are
    # at the exact MapData position but an approximate size — good enough to stop bots
    # driving through the middle of a building, not pixel-accurate.
    import re as _re, numpy as _np
    from collections import defaultdict as _dd
    templates = {}
    if a.size_templates:
        def _word(nm):
            b = _re.sub(r'_pz.*$', '', nm); b = _re.sub(r'\s*\(\d+\)$', '', b)
            t = b.split('_'); w = t[2] if len(t) >= 3 else (t[-1] if t else b)
            return _re.sub(r'\d+$', '', w)
        buckets = _dd(list)
        for v in coll.values():
            buckets[_word(v['name'])].append(v['size'])
        templates = {w: _np.median(_np.array(s), axis=0).tolist()
                     for w, s in buckets.items() if len(s) >= 5}

    boxes = set()
    used_types = set(); nomesh_types = set(); inst = 0; small = 0; approx = 0
    for mid, name, trs in groups:
        if mid is None:
            continue
        word = _re.sub(r'\d+$', '', name.split('_')[2] if len(name.split('_')) >= 3 else name)
        is_approx = False
        if mid in coll:
            c = coll[mid]['center']; half = [s/2 for s in coll[mid]['size']]; used_types.add(mid)
        elif word in templates:
            sz = templates[word]; c = [0, sz[1]/2, 0]; half = [s/2 for s in sz]; is_approx = True
        else:
            nomesh_types.add(mid); continue
        for (pos, rot, scale) in trs:
            inst += 1
            x1, z1, x2, z2 = world_box_xz(c, half, pos, rot, scale)
            x1 -= a.pad; z1 -= a.pad; x2 += a.pad; z2 += a.pad
            if (x2-x1) < a.min_size and (z2-z1) < a.min_size:
                small += 1; continue
            boxes.add((round(x1,1), round(z1,1), round(x2,1), round(z2,1)))
            if is_approx: approx += 1
    boxes = sorted(boxes)

    print(f"groups={len(groups)}  collision-types-used={len(used_types)}  "
          f"types-without-mesh={len(nomesh_types)}")
    print(f"building-instances={inst}  skipped-small={small}  unique-boxes={len(boxes)}  "
          f"(approx-from-template={approx})")
    if boxes:
        arr = np.array(boxes)
        print(f"XZ extent X[{arr[:,0].min():.0f},{arr[:,2].max():.0f}] "
              f"Z[{arr[:,1].min():.0f},{arr[:,3].max():.0f}]")

    out = f"map_{a.net_id}_obstacles.txt"
    with open(out, 'w', encoding='utf-8') as f:
        f.write(f"// {a.name} (network MapId={a.net_id}) — {len(boxes)} boxes from "
                f"{a.mapdata.split('/')[-1]}\n")
        f.write("Obstacles: new ObstacleBox[]\n{\n")
        for (x1, z1, x2, z2) in boxes:
            f.write(f"    new({x1}f, {z1}f, {x2}f, {z2}f),\n")
        f.write("}),\n")
    print("wrote", out)

if __name__ == '__main__':
    main()
