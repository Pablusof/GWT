using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace GwtEmulator.Server
{
    /// <summary>
    /// Граф маршрутов ботов, загруженный из map_NN_routes.txt (нарисованные на миникарте
    /// чёрные линии). Узлы — вершины ломаных, рёбра — сегменты между ними. Боты едут ПО этому
    /// графу к цели (враг / база), а не случайными waypoint'ами. Локальный объезд зданий
    /// по-прежнему делает серверный A* (FindPath) — но т.к. маршруты уже проложены по улицам,
    /// между соседними узлами обычно чисто.
    ///
    /// Формат файла: каждая строка — одно ребро-ломаная "X1 Z1 X2 Z2 X3 Z3 ...".
    /// Комментарии начинаются с //.
    /// </summary>
    public sealed class RouteGraph
    {
        private readonly List<(float X, float Z)> _nodes = new();
        private readonly List<List<(int to, float w)>> _adj = new();
        private const float SnapQuant = 2.0f; // узлы ближе этого сливаются в один

        public int NodeCount => _nodes.Count;
        public bool IsEmpty => _nodes.Count == 0;

        // ---- загрузка ----
        public static RouteGraph LoadOrEmpty(string path)
        {
            var g = new RouteGraph();
            try { if (File.Exists(path)) g.Load(path); }
            catch { /* пустой граф — боты откатятся на старый патруль */ }
            return g;
        }

        private void Load(string path)
        {
            var index = new Dictionary<(int, int), int>();
            int NodeId(float x, float z)
            {
                var key = ((int)MathF.Round(x / SnapQuant), (int)MathF.Round(z / SnapQuant));
                if (index.TryGetValue(key, out var id)) return id;
                id = _nodes.Count;
                _nodes.Add((x, z));
                _adj.Add(new List<(int, float)>());
                index[key] = id;
                return id;
            }
            void AddEdge(int a, int b)
            {
                if (a == b) return;
                float w = Dist(_nodes[a], _nodes[b]);
                if (!_adj[a].Any(e => e.to == b)) _adj[a].Add((b, w));
                if (!_adj[b].Any(e => e.to == a)) _adj[b].Add((a, w));
            }
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("//")) continue;
                var t = line.Split((char[])null!, StringSplitOptions.RemoveEmptyEntries);
                if (t.Length < 4) continue;
                int prev = -1;
                for (int i = 0; i + 1 < t.Length; i += 2)
                {
                    if (!float.TryParse(t[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) continue;
                    if (!float.TryParse(t[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) continue;
                    int id = NodeId(x, z);
                    if (prev >= 0) AddEdge(prev, id);
                    prev = id;
                }
            }
        }

        private static float Dist((float X, float Z) a, (float X, float Z) b)
        {
            float dx = a.X - b.X, dz = a.Z - b.Z;
            return MathF.Sqrt(dx * dx + dz * dz);
        }

        public int NearestNode(float x, float z)
        {
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < _nodes.Count; i++)
            {
                float dx = _nodes[i].X - x, dz = _nodes[i].Z - z;
                float d = dx * dx + dz * dz;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>Случайный узел графа (для патруля бота ПО маршрутам). false если граф пуст.</summary>
        public bool TryGetRandomNode(Random rng, out float x, out float z)
        {
            x = 0f; z = 0f;
            if (_nodes.Count == 0) return false;
            var n = _nodes[rng.Next(_nodes.Count)];
            x = n.X; z = n.Z;
            return true;
        }

        /// <summary>
        /// Следующая точка, к которой должен рулить бот, чтобы по графу двигаться к (goalX,goalZ).
        /// Возвращает false, если граф пуст. arriveDist — насколько близко к узлу считается «дошёл»
        /// (чтобы не втыкаться в узел, а идти к следующему).
        /// </summary>
        public bool TryGetSteerTarget(float px, float pz, float goalX, float goalZ,
                                      out float wx, out float wz, float arriveDist = 22f)
        {
            wx = goalX; wz = goalZ;
            if (_nodes.Count == 0) return false;

            int start = NearestNode(px, pz);
            int goalNode = NearestNode(goalX, goalZ);
            if (start < 0 || goalNode < 0) return false;

            // Если бот ещё далеко от своего стартового узла — сперва доехать до него (вход на маршрут).
            if (Dist(_nodes[start], (px, pz)) > arriveDist * 1.5f)
            {
                wx = _nodes[start].X; wz = _nodes[start].Z;
                // но если сам goal ближе, чем вход на маршрут — просто ехать к goal напрямую
                if (Dist((px, pz), (goalX, goalZ)) < Dist((px, pz), _nodes[start])) { wx = goalX; wz = goalZ; }
                return true;
            }

            var path = Dijkstra(start, goalNode);
            if (path == null || path.Count == 0) { wx = goalX; wz = goalZ; return true; }

            // Пропустить узлы, к которым бот уже вплотную — вернуть первый «дальний».
            foreach (int nid in path)
            {
                if (Dist(_nodes[nid], (px, pz)) > arriveDist)
                {
                    wx = _nodes[nid].X; wz = _nodes[nid].Z;
                    return true;
                }
            }
            // Все узлы пути пройдены — рулить прямо к цели.
            wx = goalX; wz = goalZ;
            return true;
        }

        private List<int>? Dijkstra(int start, int goal)
        {
            if (start == goal) return new List<int> { goal };
            var dist = new float[_nodes.Count];
            var prev = new int[_nodes.Count];
            for (int i = 0; i < dist.Length; i++) { dist[i] = float.MaxValue; prev[i] = -1; }
            dist[start] = 0;
            // простая приоритетная очередь
            var pq = new SortedSet<(float d, int n)>();
            pq.Add((0f, start));
            while (pq.Count > 0)
            {
                var (d, u) = pq.Min; pq.Remove(pq.Min);
                if (u == goal) break;
                if (d > dist[u]) continue;
                foreach (var (v, w) in _adj[u])
                {
                    float nd = d + w;
                    if (nd < dist[v])
                    {
                        pq.Remove((dist[v], v));
                        dist[v] = nd; prev[v] = u;
                        pq.Add((nd, v));
                    }
                }
            }
            if (prev[goal] == -1 && start != goal) return null;
            var path = new List<int>();
            for (int at = goal; at != -1; at = prev[at]) path.Add(at);
            path.Reverse();
            return path;
        }
    }
}
