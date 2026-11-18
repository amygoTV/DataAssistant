using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Windows.Forms;

namespace GomokuGame
{
    public class MainForm : Form
    {
        private readonly GomokuEngine engine = new GomokuEngine();
        private const int N = GomokuEngine.Size;
        private const int Cell = 32;
        private const int BoardMargin = 30;
        private int BoardPx => BoardMargin * 2 + Cell * (N - 1);

        private Panel boardPanel;
        private Label statusLabel;
        private RadioButton rbAI, rbPvP;
        private Button btnRestart, btnUndo;
        private ComboBox cmbLevel;

        private bool vsAI = true;
        private int aiLevel = 1;        // 0 简单 / 1 普通 / 2 困难
        private int hoverR = -1, hoverC = -1;

        public MainForm()
        {
            // ---- 窗体 ----
            Text = "五子棋 · Gomoku";
            BackColor = Color.FromArgb(0x2b, 0x2b, 0x2b);
            ForeColor = Color.WhiteSmoke;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(BoardPx + 170, BoardPx + 12);

            // ---- 棋盘面板 ----
            boardPanel = new Panel
            {
                Location = new Point(6, 6),
                Size = new Size(BoardPx, BoardPx),
                BackColor = Color.FromArgb(0xE3, 0xB5, 0x6E),
                BorderStyle = BorderStyle.FixedSingle,
                Cursor = Cursors.Hand
            };
            // 双缓冲，避免重绘闪烁
            boardPanel.GetType()
                .GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(boardPanel, true, null);
            boardPanel.Paint += BoardPanel_Paint;
            boardPanel.MouseClick += BoardPanel_MouseClick;
            boardPanel.MouseMove += BoardPanel_MouseMove;
            boardPanel.MouseLeave += (s, e) => { hoverR = -1; hoverC = -1; boardPanel.Invalidate(); };
            Controls.Add(boardPanel);

            // ---- 右侧控制区 ----
            int sx = BoardPx + 14;

            var modeBox = new GroupBox
            {
                Text = "对战模式",
                Location = new Point(sx, 6),
                Size = new Size(150, 74),
                ForeColor = Color.WhiteSmoke
            };
            rbAI = new RadioButton { Text = "人机对战", Location = new Point(12, 22), AutoSize = true, Checked = true };
            rbPvP = new RadioButton { Text = "双人对战", Location = new Point(12, 46), AutoSize = true };
            rbAI.CheckedChanged += ModeChanged;
            rbPvP.CheckedChanged += ModeChanged;
            modeBox.Controls.Add(rbAI);
            modeBox.Controls.Add(rbPvP);
            Controls.Add(modeBox);

            var lvlBox = new GroupBox
            {
                Text = "电脑难度",
                Location = new Point(sx, 86),
                Size = new Size(150, 74),
                ForeColor = Color.WhiteSmoke
            };
            cmbLevel = new ComboBox
            {
                Location = new Point(12, 30),
                Size = new Size(126, 23),
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbLevel.Items.AddRange(new object[] { "简单", "普通", "困难" });
            cmbLevel.SelectedIndex = 1;
            cmbLevel.SelectedIndexChanged += (s, e) => aiLevel = cmbLevel.SelectedIndex;
            lvlBox.Controls.Add(cmbLevel);
            Controls.Add(lvlBox);

            btnRestart = new Button
            {
                Text = "重新开始",
                Location = new Point(sx, 168),
                Size = new Size(150, 34)
            };
            btnRestart.Click += (s, e) => Restart();
            Controls.Add(btnRestart);

            btnUndo = new Button
            {
                Text = "悔棋",
                Location = new Point(sx, 208),
                Size = new Size(150, 34)
            };
            btnUndo.Click += (s, e) => Undo();
            Controls.Add(btnUndo);

            statusLabel = new Label
            {
                Location = new Point(sx, 256),
                Size = new Size(150, 120),
                ForeColor = Color.WhiteSmoke,
                Font = new Font("Microsoft YaHei", 11F),
                TextAlign = ContentAlignment.TopLeft
            };
            Controls.Add(statusLabel);

            UpdateStatus();
        }

        // ---------------- 绘制 ----------------
        private void BoardPanel_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var pen = new Pen(Color.FromArgb(0x6b, 0x4a, 0x2b), 1))
            {
                for (int i = 0; i < N; i++)
                {
                    int p = BoardMargin + i * Cell;
                    g.DrawLine(pen, p, BoardMargin, p, BoardMargin + (N - 1) * Cell);
                    g.DrawLine(pen, BoardMargin, p, BoardMargin + (N - 1) * Cell, p);
                }
            }

            var stars = new[] { (3, 3), (3, 11), (11, 3), (11, 11), (7, 7) };
            using (var brush = new SolidBrush(Color.FromArgb(0x6b, 0x4a, 0x2b)))
                foreach (var s in stars)
                {
                    int x = BoardMargin + s.Item2 * Cell, y = BoardMargin + s.Item1 * Cell;
                    g.FillEllipse(brush, x - 3, y - 3, 6, 6);
                }

            for (int r = 0; r < N; r++)
                for (int c = 0; c < N; c++)
                {
                    int v = engine.Board[r, c];
                    if (v != 0) DrawStone(g, r, c, v);
                }

            // 悬停预览（仅人类可落子时）
            if (!engine.GameOver && hoverR >= 0 && hoverC >= 0 &&
                engine.Board[hoverR, hoverC] == 0 && CanHumanMove())
            {
                int cur = engine.CurrentPlayer;
                using (var b = new SolidBrush(cur == 1
                    ? Color.FromArgb(0, 0, 0, 90)
                    : Color.FromArgb(255, 255, 255, 130)))
                {
                    int x = BoardMargin + hoverC * Cell, y = BoardMargin + hoverR * Cell;
                    g.FillEllipse(b, x - Radius(), y - Radius(), Radius() * 2, Radius() * 2);
                }
            }

            // 最后一手标记
            if (engine.History.Count > 0)
            {
                var last = engine.History[engine.History.Count - 1];
                int x = BoardMargin + last.c * Cell, y = BoardMargin + last.r * Cell;
                using (var p = new Pen(Color.Red, 2))
                    g.DrawRectangle(p, x - 4, y - 4, 8, 8);
            }
        }

        private int Radius() => (int)(Cell * 0.42);

        private void DrawStone(Graphics g, int r, int c, int v)
        {
            int x = BoardMargin + c * Cell, y = BoardMargin + r * Cell;
            int rad = Radius();
            var rect = new Rectangle(x - rad, y - rad, rad * 2, rad * 2);

            // 底色
            using (var baseBrush = new SolidBrush(v == 1 ? Color.Black : Color.White))
                g.FillEllipse(baseBrush, rect);
            // 左上方高光，营造立体感
            var hi = new Rectangle(rect.X + rad / 4, rect.Y + rad / 4, rad, rad);
            using (var hiBrush = new SolidBrush(v == 1 ? Color.FromArgb(95, 95, 95) : Color.FromArgb(235, 235, 235)))
                g.FillEllipse(hiBrush, hi);
            // 描边
            using (var pen = new Pen(v == 1 ? Color.Black : Color.Gray, 1))
                g.DrawEllipse(pen, rect);
        }

        // ---------------- 交互 ----------------
        private void BoardPanel_MouseClick(object sender, MouseEventArgs e)
        {
            if (engine.GameOver) return;
            if (vsAI && engine.CurrentPlayer == GomokuEngine.White) return; // 电脑回合
            int c = (e.X - BoardMargin + Cell / 2) / Cell;
            int r = (e.Y - BoardMargin + Cell / 2) / Cell;
            if (r < 0 || r >= N || c < 0 || c >= N) return;
            if (engine.Board[r, c] != 0) return;
            HumanMove(r, c);
        }

        private void BoardPanel_MouseMove(object sender, MouseEventArgs e)
        {
            int c = (e.X - BoardMargin + Cell / 2) / Cell;
            int r = (e.Y - BoardMargin + Cell / 2) / Cell;
            bool valid = r >= 0 && r < N && c >= 0 && c < N &&
                         engine.Board[r, c] == 0 && !engine.GameOver && CanHumanMove();
            if (valid && (hoverR != r || hoverC != c))
            {
                hoverR = r; hoverC = c; boardPanel.Invalidate();
            }
            else if (!valid && hoverR >= 0)
            {
                hoverR = -1; hoverC = -1; boardPanel.Invalidate();
            }
        }

        private bool CanHumanMove()
        {
            if (!vsAI) return true;
            return engine.CurrentPlayer == GomokuEngine.Black; // 人机模式人类执黑
        }

        private void HumanMove(int r, int c)
        {
            engine.Place(r, c, engine.CurrentPlayer);
            boardPanel.Invalidate();
            if (engine.GameOver) { UpdateStatus(); return; }

            engine.CurrentPlayer = engine.CurrentPlayer == 1 ? 2 : 1;
            UpdateStatus();

            if (vsAI && engine.CurrentPlayer == GomokuEngine.White && !engine.GameOver)
            {
                var timer = new Timer { Interval = 280 };
                timer.Tick += (s, ev) => { timer.Stop(); timer.Dispose(); AIMove(); };
                timer.Start();
            }
        }

        private void AIMove()
        {
            if (engine.GameOver) return;
            var mv = engine.GetAIMove(GomokuEngine.White, aiLevel);
            engine.Place(mv.r, mv.c, GomokuEngine.White);
            engine.CurrentPlayer = GomokuEngine.Black;
            boardPanel.Invalidate();
            UpdateStatus();
        }

        private void ModeChanged(object sender, EventArgs e)
        {
            vsAI = rbAI.Checked;
            Restart();
        }

        private void Restart()
        {
            engine.Reset();
            hoverR = -1; hoverC = -1;
            boardPanel.Invalidate();
            UpdateStatus();
        }

        private void Undo()
        {
            if (engine.History.Count == 0) return;
            if (vsAI)
            {
                engine.Undo(); // 电脑一手
                if (engine.History.Count > 0) engine.Undo(); // 人类一手
            }
            else
            {
                engine.Undo();
            }
            hoverR = -1; hoverC = -1;
            boardPanel.Invalidate();
            UpdateStatus();
        }

        private void UpdateStatus()
        {
            if (engine.GameOver)
            {
                string who = engine.Winner == 1 ? "黑方" : "白方";
                if (vsAI) who = engine.Winner == 1 ? "你（黑）赢了！" : "电脑（白）赢了！";
                statusLabel.Text = "游戏结束\n" + who + " 获胜 🎉\n\n点击「重新开始」再来一局";
                return;
            }
            if (vsAI)
                statusLabel.Text = engine.CurrentPlayer == 1
                    ? "轮到你（黑）落子"
                    : "电脑（白）思考中…";
            else
                statusLabel.Text = engine.CurrentPlayer == 1
                    ? "轮到黑方落子"
                    : "轮到白方落子";
            statusLabel.Text += "\n\n（黑棋先行）";
        }
    }
}
