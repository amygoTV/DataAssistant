using System;
using System.Collections.Generic;

namespace GomokuGame
{
    /// <summary>
    /// 五子棋核心逻辑：棋盘状态、胜负判定、AI 走法。
    /// 棋盘为 15x15 标准格；0=空，1=黑，2=白。
    /// </summary>
    public class GomokuEngine
    {
        public const int Size = 15;
        public const int Empty = 0;
        public const int Black = 1;
        public const int White = 2;

        public int[,] Board { get; private set; }
        public int CurrentPlayer { get; set; }
        public bool GameOver { get; set; }
        public int Winner { get; set; }

        // 历史记录：(行, 列, 棋手)，用于悔棋
        public List<(int r, int c, int p)> History { get; } = new List<(int, int, int)>();

        public GomokuEngine()
        {
            Board = new int[Size, Size];
            Reset();
        }

        public void Reset()
        {
            Array.Clear(Board, 0, Board.Length);
            CurrentPlayer = Black;
            GameOver = false;
            Winner = Empty;
            History.Clear();
        }

        public bool Place(int r, int c, int player)
        {
            if (r < 0 || r >= Size || c < 0 || c >= Size) return false;
            if (Board[r, c] != Empty) return false;
            Board[r, c] = player;
            History.Add((r, c, player));
            if (CheckWin(r, c, player))
            {
                GameOver = true;
                Winner = player;
            }
            return true;
        }

        /// <summary>悔棋一步，并恢复轮次。</summary>
        public void Undo()
        {
            if (History.Count == 0) return;
            var last = History[History.Count - 1];
            Board[last.r, last.c] = Empty;
            History.RemoveAt(History.Count - 1);
            GameOver = false;
            Winner = Empty;
            CurrentPlayer = last.p; // 回到刚走的那一方
        }

        /// <summary>从 (r,c) 出发，沿四个方向检测是否形成五连。</summary>
        public bool CheckWin(int r, int c, int player)
        {
            int[][] dirs = { new[] { 1, 0 }, new[] { 0, 1 }, new[] { 1, 1 }, new[] { 1, -1 } };
            foreach (var d in dirs)
            {
                int count = 1;
                int i = r + d[0], j = c + d[1];
                while (InBounds(i, j) && Board[i, j] == player) { count++; i += d[0]; j += d[1]; }
                i = r - d[0]; j = c - d[1];
                while (InBounds(i, j) && Board[i, j] == player) { count++; i -= d[0]; j -= d[1]; }
                if (count >= 5) return true;
            }
            return false;
        }

        /// <summary>为指定棋手计算最佳落子点。level: 0=简单, 1=普通, 2=困难。</summary>
        public (int r, int c) GetAIMove(int aiPlayer, int level = 1)
        {
            int human = aiPlayer == Black ? White : Black;

            // 1) 自己能赢，直接赢
            var win = FindWinningMove(aiPlayer);
            if (win.HasValue) return win.Value;
            // 2) 对手能赢，必须堵
            var block = FindWinningMove(human);
            if (block.HasValue) return block.Value;

            // 简单难度有一定随机性，变弱
            if (level == 0 && new Random().Next(100) < 45)
            {
                var rnd = RandomNearby();
                if (rnd.HasValue) return rnd.Value;
            }

            double defenseWeight = level == 2 ? 1.15 : (level == 0 ? 0.6 : 0.9);

            int bestScore = -1;
            (int r, int c) best = (-1, -1);
            for (int r = 0; r < Size; r++)
            {
                for (int c = 0; c < Size; c++)
                {
                    if (Board[r, c] != Empty) continue;
                    if (!HasNeighbor(r, c)) continue; // 只评估已有棋子附近的空点
                    int atk = EvaluatePoint(r, c, aiPlayer);
                    int def = EvaluatePoint(r, c, human);
                    int score = (int)(atk + def * defenseWeight);
                    if (score > bestScore) { bestScore = score; best = (r, c); }
                }
            }
            if (best.r < 0) return (7, 7);
            return best;
        }

        private (int r, int c)? FindWinningMove(int player)
        {
            for (int r = 0; r < Size; r++)
                for (int c = 0; c < Size; c++)
                {
                    if (Board[r, c] != Empty) continue;
                    Board[r, c] = player;
                    bool win = CheckWin(r, c, player);
                    Board[r, c] = Empty;
                    if (win) return (r, c);
                }
            return null;
        }

        /// <summary>评估某个空点若被 player 落子后的价值（四方向连子评分之和）。</summary>
        private int EvaluatePoint(int r, int c, int player)
        {
            int[][] dirs = { new[] { 1, 0 }, new[] { 0, 1 }, new[] { 1, 1 }, new[] { 1, -1 } };
            int total = 0;
            foreach (var d in dirs)
            {
                int count = 1;       // 含候选点本身
                int openEnds = 0;

                int i = r + d[0], j = c + d[1];
                while (InBounds(i, j) && Board[i, j] == player) { count++; i += d[0]; j += d[1]; }
                if (InBounds(i, j) && Board[i, j] == Empty) openEnds++;

                i = r - d[0]; j = c - d[1];
                while (InBounds(i, j) && Board[i, j] == player) { count++; i -= d[0]; j -= d[1]; }
                if (InBounds(i, j) && Board[i, j] == Empty) openEnds++;

                total += ScoreLine(count, openEnds);
            }
            return total;
        }

        private int ScoreLine(int count, int openEnds)
        {
            if (count >= 5) return 1000000;
            if (openEnds == 0) return 0; // 两端被堵，死棋
            switch (count)
            {
                case 4: return openEnds == 2 ? 100000 : 10000;
                case 3: return openEnds == 2 ? 5000 : 500;
                case 2: return openEnds == 2 ? 200 : 50;
                case 1: return openEnds == 2 ? 10 : 5;
                default: return 0;
            }
        }

        private bool HasNeighbor(int r, int c)
        {
            for (int dr = -2; dr <= 2; dr++)
                for (int dc = -2; dc <= 2; dc++)
                {
                    int nr = r + dr, nc = c + dc;
                    if (nr < 0 || nr >= Size || nc < 0 || nc >= Size) continue;
                    if (Board[nr, nc] != Empty) return true;
                }
            return false;
        }

        private (int r, int c)? RandomNearby()
        {
            var list = new List<(int, int)>();
            for (int r = 0; r < Size; r++)
                for (int c = 0; c < Size; c++)
                    if (Board[r, c] == Empty && HasNeighbor(r, c)) list.Add((r, c));
            if (list.Count == 0) return null;
            return list[new Random().Next(list.Count)];
        }

        private bool InBounds(int r, int c) => r >= 0 && r < Size && c >= 0 && c < Size;
    }
}
