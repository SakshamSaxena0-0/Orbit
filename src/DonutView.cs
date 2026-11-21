using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Orbit
{
    enum HitKind { Outside, Center, Slice, Remove, EmptyRing }

    struct DonutHit
    {
        public readonly HitKind Kind;
        public readonly int Slot;
        public DonutHit(HitKind kind, int slot) { Kind = kind; Slot = slot; }
        public bool OnSlice { get { return Kind == HitKind.Slice || Kind == HitKind.Remove; } }
    }

    sealed class DonutView : FrameworkElement
    {
        public event Action<int> Invoked;
        public event Action<int> ContextRequested;
        public event Action CenterInvoked;
        public event Action DismissRequested;
        public event Action AddRequested;
        public event Action<int, int> Reordered;
        public event Action<int> RemoveRequested;
        public event Action<int> RenameRequested;
        public event Action<string[], int> ItemsDropped;
        public event Action Closed;
        public event Action<int> MiddleInvoked;

        public const double Gap = 6;
        public const double PopOut = 11, Rim = 30;
        const double Base = 22;
        const double Stiffness = 320, Damping = 21;
        const double OpenDur = 0.30, AppearDur = 0.26, Stagger = 0.02;
        const double TurnDur = 0.50;
        const double CloseDur = 0.15, CloseSlice = 0.11, CloseStagger = 0.018;
        const double WheelQuiet = 0.25;
        public const int IconPixels = 96;

        List<AppEntry> _apps = new List<AppEntry>();
        IconCache _icons;
        double _outer = 240, _inner = 72, _sliceWidth = 54, _corner = 12, _gap = 6, _shortRays = 1;
        int _perFace = 8;
        bool _editMode, _slim = true, _plate = true, _fan = true;

        int _face, _queued, _turnDir, _outFace;
        SlotVisual[] _outgoing;
        readonly double[] _outPop = new double[16];
        int _hover = -1, _key = -1, _expanded = -1;
        bool _hubHot, _removeHot, _ringHot;
        DonutHit _press = new DonutHit(HitKind.Outside, -1);
        Point _pressPoint, _mouse;
        bool _dragging, _externalDrag;
        int _dragFrom = -1, _dropSlot = -1;

        readonly Stopwatch _clock = Stopwatch.StartNew();
        double _last, _openStart = -10, _appearStart = -10, _turnStart = -1, _closeStart = -1, _lastWheel = -10;
        bool _closing, _closeDone, _looping, _drawn;
        readonly double[] _pop = new double[16], _popV = new double[16];
        double _hubPop, _hubPopV;

        Geometry[] _wedges = new Geometry[0];
        Geometry _ring;
        Brush _shadow, _base, _hubBase, _sheen, _badgeFill, _plateFill, _lift;
        Pen _tileRim, _accentPen, _dashPen, _badgeCross, _badgeCrossLit, _plateRim;
        Color _accent = Color.FromRgb(0x8A, 0xB4, 0xFF);
        readonly FontFamily _family = new FontFamily("Segoe UI Variable Text, Segoe UI");
        readonly Dictionary<string, FormattedText> _texts = new Dictionary<string, FormattedText>();
        readonly Dictionary<string, double> _labelSizes = new Dictionary<string, double>();
        readonly Dictionary<string, Brush> _tiles = new Dictionary<string, Brush>();
        static readonly Brush White = Frozen(new SolidColorBrush(Color.FromArgb(0xF5, 255, 255, 255)));
        static readonly Brush Soft = Frozen(new SolidColorBrush(Color.FromArgb(0xB8, 255, 255, 255)));
        static readonly Brush Faint = Frozen(new SolidColorBrush(Color.FromArgb(0x5C, 255, 255, 255)));
        static readonly Brush TextShadow = Frozen(new SolidColorBrush(Color.FromArgb(0x8C, 0, 0, 0)));
        static readonly Brush HitFill = Frozen(new SolidColorBrush(Color.FromArgb(0x01, 0, 0, 0)));
        static readonly Pen HitMargin = Frozen(new Pen(Brushes.Black, 12));

        public DonutView()
        {
            Focusable = true;
            AllowDrop = true;
            FocusVisualStyle = null;
            _visuals = new VisualCollection(this);
            BuildLayers();
            RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            var defaults = new Settings();
            defaults.Normalize();
            Configure(defaults, null);
        }

        public double ViewWidth { get { return 2 * (_outer + PopOut + Rim); } }
        public double ViewHeight { get { return _fan ? _outer + PopOut + Rim + Base : ViewWidth; } }
        public Point Anchor { get { return Center; } }
        public bool IsClosing { get { return _closing; } }
        public int Faces { get { return Math.Max(1, (_apps.Count + _perFace - 1) / _perFace); } }
        int SlotCount { get { return Math.Max(0, Math.Min(_perFace, _apps.Count - _face * _perFace)); } }
        int Active { get { return _key >= 0 ? _key : _hover; } }
        Point Center { get { return new Point(ViewWidth / 2, _outer + PopOut + Rim); } }
        double SpanAngle { get { return _fan ? Math.PI : 2 * Math.PI; } }
        double SlotAngle(int n) { return SpanAngle / Math.Max(1, n); }
        int Global(int slot) { return _face * _perFace + slot; }
        double Now() { return _clock.Elapsed.TotalSeconds; }

        public bool EditMode
        {
            get { return _editMode; }
            set { _editMode = value; Animate(); }
        }

        public void Configure(Settings s, IconCache icons)
        {
            _outer = s.OuterRadius;
            _inner = s.OuterRadius * s.HoleRatio;
            _sliceWidth = s.SliceWidth;
            _slim = s.IsSlim;
            _fan = s.IsFan;
            _shortRays = s.ShortRays;
            _plate = s.Plate != false;
            _corner = s.Roundness;
            _gap = s.Gap;
            _perFace = s.PerFace;
            try { _accent = (Color)ColorConverter.ConvertFromString(s.Accent); }
            catch (FormatException) { _accent = Color.FromRgb(0x8A, 0xB4, 0xFF); }
            if (icons != null && icons != _icons)
            {
                _icons = icons;
                _icons.Loaded += OnIconLoaded;
            }
            Width = ViewWidth;
            Height = ViewHeight;
            BuildBrushes();
            EndTurn();
            Rebuild();
        }

        void OnIconLoaded()
        {
            for (int i = 0; i < _slotVisuals.Length && i < SlotCount; i++) RenderSlot(i, SlotCount);
            RenderState();
        }

        public void SetApps(List<AppEntry> apps)
        {
            EndTurn();
            _apps = apps ?? new List<AppEntry>();
            if (_face >= Faces) _face = Faces - 1;
            _key = -1;
            _texts.Clear();
            Rebuild();
            Track(HitTest(_mouse));
        }

        public void PlayOpen()
        {
            _closing = _closeDone = false;
            _hover = _key = _expanded = -1;
            _hubHot = _removeHot = _ringHot = _dragging = _externalDrag = false;
            _dragFrom = _dropSlot = -1;
            EndTurn();
            _face = 0;
            _openFrames = 0;
            _openStart = _appearStart = Now();
            TickClock();
            Array.Clear(_pop, 0, _pop.Length);
            Array.Clear(_popV, 0, _popV.Length);
            _hubPop = _hubPopV = 0;
            _texts.Clear();
            Rebuild();
            Animate();
        }

        readonly DispatcherTimer _minute = new DispatcherTimer(DispatcherPriority.Background);

        void TickClock()
        {
            if (_minute.Tag == null)
            {
                _minute.Tag = this;
                _minute.Tick += delegate { RenderState(); TickClock(); };
            }
            DateTime now = DateTime.Now;
            _minute.Interval = TimeSpan.FromMilliseconds((60 - now.Second) * 1000 - now.Millisecond + 50);
            _minute.Start();
        }

        public void SetExpanded(int globalIndex)
        {
            int slot = globalIndex - _face * _perFace;
            _expanded = slot >= 0 && slot < SlotCount ? slot : -1;
            Animate();
        }

        public Point TipOf(int globalIndex, out Vector direction)
        {
            int n = SlotCount, slot = globalIndex - _face * _perFace;
            double mid = SliceMid(slot, n);
            direction = new Vector(Math.Cos(mid), Math.Sin(mid));
            return Center + direction * (OuterOf(slot, n) + PopOut);
        }

        public int FramesSinceOpen { get { return _openFrames; } }
        int _openFrames;

        public Rect HubProbe
        {
            get
            {
                Point c = Center;
                double hr = _inner - Gap;
                return new Rect(c.X - hr * 0.5, _fan ? c.Y - hr * 0.55 : c.Y - hr * 0.15, hr, hr * 0.3);
            }
        }

        public void PlayClose()
        {
            if (_closing || _closeDone) return;
            _closing = true;
            _closeStart = Now();
            _minute.Stop();
            if (IsMouseCaptured) ReleaseMouseCapture();
            Animate();
        }

        public bool Flip(int direction)
        {
            if (Faces < 2 || _closing) return false;
            _dropSlot = -1;
            if (_turnStart >= 0)
                _queued = Math.Max(-3, Math.Min(3, _queued + direction));
            else StartTurn(direction);
            Animate();
            return true;
        }

        void StartTurn(int direction)
        {
            _outgoing = _slotVisuals;
            _outFace = _face;
            Array.Copy(_pop, _outPop, _pop.Length);
            Array.Clear(_pop, 0, _pop.Length);
            Array.Clear(_popV, 0, _popV.Length);
            _face = Mod(_face + direction, Faces);
            _turnDir = direction;
            _turnStart = Now();
            _appearStart = -10;
            _key = _hover = -1;
            if (_fan) _slotLayer.Clip = new RectangleGeometry(new Rect(-ViewWidth, -ViewHeight, 3 * ViewWidth, ViewHeight + Center.Y + 4));
            Rebuild();
        }

        void EndTurn()
        {
            if (_outgoing != null)
                foreach (SlotVisual sv in _outgoing) _slotLayer.Children.Remove(sv.Root);
            _outgoing = null;
            _turnStart = -1;
            _queued = 0;
            _slotLayer.Clip = null;
        }

        bool Turning { get { return _turnStart >= 0; } }

        double TurnProgress(double now)
        {
            if (!Turning) return 1;
            double t = Math.Min(Math.Max(0, now - _turnStart), TurnDur);
            return DetentSpring(t) + (1 - DetentSpring(TurnDur)) * Math.Pow(t / TurnDur, 4);
        }

        static double DetentSpring(double t)
        {
            const double w = 16, zeta = 0.75;
            double damped = w * Math.Sqrt(1 - zeta * zeta);
            return 1 - Math.Exp(-zeta * w * t) * (Math.Cos(damped * t) + zeta / Math.Sqrt(1 - zeta * zeta) * Math.Sin(damped * t));
        }

        double TurnAngle(double progress, bool outgoing)
        {
            double span = _fan ? Math.PI : Math.PI / 2;
            return outgoing ? -_turnDir * progress * span : _turnDir * (1 - progress) * span;
        }

        double HorizonFade(double angle, int n)
        {
            if (!_fan) return 1;
            double a = Math.Atan2(Math.Sin(angle), Math.Cos(angle));
            double elevation = a <= 0 ? Math.Min(a + Math.PI, -a) : -Math.Min(a, Math.PI - a);
            double rest = SlotAngle(n) / 2, top = Math.Max(rest - 0.10, rest * 0.4), below = 0.12;
            double f = Clamp01((elevation + below) / (top + below));
            return f * f * (3 - 2 * f);
        }

        void DragTo(Point p)
        {
            DonutHit h = HitTest(p);
            _dropSlot = h.OnSlice && !Turning ? h.Slot : -1;
            _hover = -1;
        }

        void BuildBrushes()
        {
            Point c = Center;
            double R = _outer;
            var shadow = new RadialGradientBrush();
            if (_slim)
            {
                shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x48, 0, 0, 0), 0));
                shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x38, 0, 0, 0), 0.45));
                shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x1A, 0, 0, 0), 0.8));
                shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
            }
            else
            {
                shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, 0, 0, 0), 0));
                shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x40, 0, 0, 0), 0.84));
                shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x1E, 0, 0, 0), 0.92));
                shadow.GradientStops.Add(new GradientStop(Color.FromArgb(0x00, 0, 0, 0), 1));
            }
            _shadow = Frozen(shadow);

            byte baseAlpha = (byte)(!_slim ? 0x5E : _plate ? 0x48 : 0x7C);
            _base = Frozen(new SolidColorBrush(Color.FromArgb(baseAlpha, 0x0E, 0x11, 0x17)));
            _lift = _slim && _plate ? Frozen(new SolidColorBrush(Color.FromArgb(0x16, 255, 255, 255))) : null;
            _plateFill = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0x0A, 0x0C, 0x11)));
            _plateRim = Frozen(new Pen(RimBrush(c, R, 0x80, 0x30, 0x0C, 0x18, 0x60), 1));
            _hubBase = Frozen(new SolidColorBrush(Color.FromArgb((byte)(_slim ? 0x86 : 0x78), 0x0E, 0x11, 0x17)));

            var sheen = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(c.X, c.Y - R), EndPoint = new Point(c.X, c.Y + R) };
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x30, 255, 255, 255), 0));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x0A, 255, 255, 255), 0.55));
            sheen.GradientStops.Add(new GradientStop(Color.FromArgb(0x16, 255, 255, 255), 1));
            _sheen = Frozen(sheen);

            _tileRim = Frozen(new Pen(RimBrush(c, R, 0xE0, 0x50, 0x16, 0x2A, 0x9C), 1.1));
            _accentPen = Frozen(new Pen(Frozen(new SolidColorBrush(_accent)), 2.2));
            var dash = new Pen(Frozen(new SolidColorBrush(Color.FromArgb(0x9C, _accent.R, _accent.G, _accent.B))), 1.4);
            dash.DashStyle = new DashStyle(new double[] { 3, 3 }, 0);
            _dashPen = Frozen(dash);
            _badgeFill = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1F, 0x26)));
            _badgeCross = Frozen(new Pen(White, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
            _badgeCrossLit = Frozen(new Pen(Glow.InkBrush, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        }

        static Brush RimBrush(Point c, double R, byte a0, byte a1, byte a2, byte a3, byte a4)
        {
            var b = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(c.X - R, c.Y - R), EndPoint = new Point(c.X + R, c.Y + R) };
            b.GradientStops.Add(new GradientStop(Color.FromArgb(a0, 255, 255, 255), 0));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(a1, 255, 255, 255), 0.28));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(a2, 255, 255, 255), 0.52));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(a3, 255, 255, 255), 0.76));
            b.GradientStops.Add(new GradientStop(Color.FromArgb(a4, 255, 255, 255), 1));
            return Frozen(b);
        }

        void Rebuild()
        {
            Point c = Center;
            int n = SlotCount;
            double span = SlotAngle(n);
            _wedges = new Geometry[n];
            _ring = _fan ? UpperHalf(Ring(c, _inner, _outer)) : Ring(c, _inner, _outer);
            double wIn, wOut;
            BladeWidths(n, out wIn, out wOut);
            for (int i = 0; i < n; i++)
            {
                double mid = SliceMid(i, n);
                if (_slim) _wedges[i] = Blade(c, _inner, OuterOf(i, n), mid, wIn, wOut, _corner);
                else _wedges[i] = n == 1 && !_fan ? _ring : Wedge(c, _inner, _outer, mid, span / 2, _gap, _corner);
            }
            RenderAll();
        }

        double OuterOf(int i, int n)
        {
            if (!_slim || n < 2 || i % 2 == 1) return _outer;
            return _inner + (_outer - _inner) * _shortRays;
        }

        double SliceMid(int i, int n)
        {
            return _fan ? -Math.PI + (i + 0.5) * SlotAngle(n) : -Math.PI / 2 + i * SlotAngle(n);
        }

        void BladeWidths(int n, out double wIn, out double wOut)
        {
            double s = n > 1 || _fan ? Math.Sin(SlotAngle(n) / 2) : 1;
            wOut = Math.Max(24, Math.Min(_sliceWidth, 2 * _outer * s - 14));
            wIn = Math.Max(14, Math.Min(wOut * 0.7, 2 * _inner * s - 8));
        }

        double IconSize(int n)
        {
            if (_slim)
            {
                double wIn, wOut;
                BladeWidths(n, out wIn, out wOut);
                return Math.Min(40, wOut * 0.76);
            }
            double band = _outer - _inner;
            double chord = n <= 1 && !_fan ? band * 2 : SlotAngle(n) * (_inner + _outer) / 2;
            return Math.Max(20, Math.Min(46, Math.Min(band * 0.45, chord * 0.58)));
        }

        double LabelWidth(int n)
        {
            double chord = n <= 1 && !_fan ? (_outer - _inner) * 2 : SlotAngle(n) * (_inner + _outer) / 2;
            return Math.Max(36, Math.Min(_outer - _inner, chord * 0.92));
        }

        double LabelSize(string name, double maxWidth)
        {
            string key = name + "\u0001" + maxWidth;
            double size;
            if (_labelSizes.TryGetValue(key, out size)) return size;
            var typeface = new Typeface(_family, FontStyles.Normal, FontWeights.Medium, FontStretches.Normal);
            double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            size = 10;
            foreach (double candidate in new[] { 11.5, 11, 10.5 })
            {
                var ft = new FormattedText(name ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, candidate, White, ppd);
                if (ft.WidthIncludingTrailingWhitespace <= maxWidth) { size = candidate; break; }
            }
            _labelSizes[key] = size;
            return size;
        }

        Point IconCenter(int i, int n)
        {
            if (_slim)
            {
                double wIn, wOut;
                BladeWidths(n, out wIn, out wOut);
                return Polar(Center, OuterOf(i, n) - wOut / 2, SliceMid(i, n));
            }
            Point p = Polar(Center, (_inner + _outer) / 2, SliceMid(i, n));
            return new Point(p.X, p.Y - 9);
        }

        Vector PopVector(int i, int n) { return PopVector(i, n, _pop[i]); }

        Vector PopVector(int i, int n, double pop)
        {
            double mid = SliceMid(i, n);
            double amount = n == 1 && !_slim ? 0 : PopOut * pop;
            return new Vector(Math.Cos(mid) * amount, Math.Sin(mid) * amount);
        }

        double SpokeAngle(int i, int n)
        {
            if (!_slim) return 0;
            double mid = SliceMid(i, n), degrees = mid * 180 / Math.PI;
            return Math.Cos(mid) < -1e-3 ? degrees + 180 : degrees;
        }

        Point BadgeCenter(int i, int n)
        {
            double reach = IconSize(n) * 0.46;
            var turn = Matrix.Identity;
            turn.Rotate(SpokeAngle(i, n));
            return IconCenter(i, n) + turn.Transform(new Vector(-reach, -reach)) + PopVector(i, n);
        }

        static Point Polar(Point c, double radius, double angle)
        {
            return new Point(c.X + radius * Math.Cos(angle), c.Y + radius * Math.Sin(angle));
        }

        static Point Along(Point from, Point to, double distance)
        {
            Vector v = to - from;
            double len = v.Length;
            return len < 1e-6 ? from : from + v * (distance / len);
        }

        static Geometry Blade(Point c, double ri, double ro, double mid, double wIn, double wOut, double radius)
        {
            var u = new Vector(Math.Cos(mid), Math.Sin(mid));
            var v = new Vector(-u.Y, u.X);
            var corners = new[]
            {
                c + u * ri + v * (wIn / 2),
                c + u * ro + v * (wOut / 2),
                c + u * ro - v * (wOut / 2),
                c + u * ri - v * (wIn / 2),
            };
            return RoundedPolygon(corners, Math.Min(radius, wIn / 2 - 0.5));
        }

        static Geometry RoundedPolygon(Point[] corners, double radius)
        {
            int n = corners.Length;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                for (int i = 0; i < n; i++)
                {
                    Point at = corners[i];
                    Point start = Along(at, corners[(i + n - 1) % n], radius), end = Along(at, corners[(i + 1) % n], radius);
                    if (i == 0) ctx.BeginFigure(start, true, true);
                    else ctx.LineTo(start, true, true);
                    ctx.QuadraticBezierTo(at, end, true, true);
                }
            }
            return Frozen(geo);
        }

        Geometry UpperHalf(Geometry g)
        {
            var above = new RectangleGeometry(new Rect(0, 0, ViewWidth, Center.Y));
            return Frozen(Geometry.Combine(g, above, GeometryCombineMode.Intersect, null));
        }

        static Geometry Dome(Point c, double r, double k)
        {
            double turn = k / r;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(c.X - r + k, c.Y), true, true);
                ctx.QuadraticBezierTo(new Point(c.X - r, c.Y), Polar(c, r, -Math.PI + turn), true, true);
                ctx.ArcTo(Polar(c, r, -turn), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                ctx.QuadraticBezierTo(new Point(c.X + r, c.Y), new Point(c.X + r - k, c.Y), true, true);
            }
            return Frozen(geo);
        }

        static Geometry Arch(Point c, double r)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(c.X - r, c.Y), false, false);
                ctx.ArcTo(new Point(c.X, c.Y - r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                ctx.ArcTo(new Point(c.X + r, c.Y), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
            }
            return Frozen(geo);
        }

        static Geometry Ring(Point c, double inner, double outer)
        {
            var g = new GeometryGroup { FillRule = FillRule.EvenOdd };
            g.Children.Add(new EllipseGeometry(c, outer, outer));
            g.Children.Add(new EllipseGeometry(c, inner, inner));
            return Frozen(g);
        }

        static Geometry Wedge(Point c, double ri, double ro, double mid, double half, double gap, double radius)
        {
            double g = gap / 2;
            double dOut = Math.Asin(Math.Min(1, g / ro)), dIn = Math.Asin(Math.Min(1, g / ri));
            double oa0 = mid - half + dOut, oa1 = mid + half - dOut;
            double ia0 = mid - half + dIn, ia1 = mid + half - dIn;
            if (ia1 < ia0) ia0 = ia1 = mid;
            Point A = Polar(c, ro, oa0), B = Polar(c, ro, oa1), C = Polar(c, ri, ia1), D = Polar(c, ri, ia0);
            double edge = (B - C).Length;
            double ko = Math.Min(radius, Math.Min(edge * 0.45, ro * (oa1 - oa0) * 0.45));
            double ki = Math.Min(radius * 0.7, Math.Min(edge * 0.45, ri * (ia1 - ia0) * 0.45));

            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                Point p1 = Polar(c, ro, oa0 + ko / ro);
                Point p2 = Polar(c, ro, oa1 - ko / ro);
                ctx.BeginFigure(p1, true, true);
                ctx.ArcTo(p2, new Size(ro, ro), 0, (oa1 - oa0) - 2 * ko / ro > Math.PI, SweepDirection.Clockwise, true, true);
                ctx.QuadraticBezierTo(B, Along(B, C, ko), true, true);
                ctx.LineTo(Along(C, B, ki), true, true);
                if (ia1 - ia0 > 1e-6)
                {
                    ctx.QuadraticBezierTo(C, Polar(c, ri, ia1 - ki / ri), true, true);
                    ctx.ArcTo(Polar(c, ri, ia0 + ki / ri), new Size(ri, ri), 0, (ia1 - ia0) - 2 * ki / ri > Math.PI, SweepDirection.Counterclockwise, true, true);
                    ctx.QuadraticBezierTo(D, Along(D, A, ki), true, true);
                }
                else
                {
                    ctx.LineTo(C, true, true);
                    ctx.LineTo(Along(D, A, ki), true, true);
                }
                ctx.LineTo(Along(A, D, ko), true, true);
                ctx.QuadraticBezierTo(A, p1, true, true);
            }
            return Frozen(geo);
        }

        public DonutHit HitTest(Point p)
        {
            Vector v = p - Center;
            double d = v.Length;
            if (_fan && v.Y > Gap) return new DonutHit(HitKind.Outside, -1);
            if (d < _inner - Gap / 2) return new DonutHit(HitKind.Center, -1);
            if (d > _outer + PopOut + 4) return new DonutHit(HitKind.Outside, -1);
            int n = SlotCount;
            if (n == 0) return d <= _outer ? new DonutHit(HitKind.EmptyRing, -1) : new DonutHit(HitKind.Outside, -1);
            double span = SlotAngle(n), a;
            if (_fan)
            {
                a = Math.Atan2(v.Y, v.X) + Math.PI;
                if (a > 1.5 * Math.PI) a -= 2 * Math.PI;
            }
            else
            {
                a = (Math.Atan2(v.Y, v.X) + Math.PI / 2 + span / 2) % (2 * Math.PI);
                if (a < 0) a += 2 * Math.PI;
            }
            int slot = Math.Max(0, Math.Min(n - 1, (int)Math.Floor(a / span)));
            if (_editMode && (p - BadgeCenter(slot, n)).Length <= 11) return new DonutHit(HitKind.Remove, slot);
            Point local = p - PopVector(slot, n);
            if (_wedges[slot].FillContains(local) || _wedges[slot].StrokeContains(HitMargin, local))
                return new DonutHit(HitKind.Slice, slot);
            return new DonutHit(HitKind.Outside, -1);
        }

        void Animate()
        {
            RenderState();
            ApplyMotion(Now());
            if (_looping && Now() - _last > 0.25)
            {
                Log.Write("wheel: frames had stopped for " + (int)((Now() - _last) * 1000) + " ms; restarting them");
                CompositionTarget.Rendering -= OnFrame;
                _looping = false;
            }
            if (_looping) return;
            _looping = true;
            _last = _loopStart = Now();
            _frames = 0;
            _worstGap = 0;
            CompositionTarget.Rendering += OnFrame;
        }

        double _loopStart, _worstGap;
        int _frames;

        void OnFrame(object sender, EventArgs e)
        {
            double now = Now();
            double gap = now - _last;
            if (_frames++ > 0 && gap > _worstGap) _worstGap = gap;
            _openFrames++;
            double dt = Math.Min(0.05, Math.Max(0, gap));
            _last = now;
            bool more = Step(now, dt);
            ApplyMotion(now);
            if (more) return;
            CompositionTarget.Rendering -= OnFrame;
            _looping = false;
            double span = now - _loopStart;
            if (span > 0.1) Log.Trace(string.Format(CultureInfo.InvariantCulture, "animation: {0} frames in {1:0} ms = {2:0} fps, worst gap {3:0.0} ms",
                _frames, span * 1000, _frames / span, _worstGap * 1000));
        }

        bool Step(double now, double dt)
        {
            bool more = false;
            int n = SlotCount;

            if (Turning)
            {
                if (now - _turnStart >= TurnDur)
                {
                    int queued = _queued;
                    EndTurn();
                    if (queued != 0 && !_closing)
                    {
                        StartTurn(Math.Sign(queued));
                        _queued = queued - Math.Sign(queued);
                        n = SlotCount;
                    }
                    else if (_dragging)
                    {
                        DragTo(_mouse);
                        RenderState();
                    }
                    else Track(HitTest(_mouse));
                }
                more = true;
            }

            for (int i = 0; i < n; i++)
            {
                bool lit = (i == Active && !_dragging && !Turning) || i == _expanded || (i == _dropSlot && (_dragging || _externalDrag));
                if (_dragging && Global(i) == _dragFrom) lit = false;
                if (Spring(ref _pop[i], ref _popV[i], lit ? 1 : 0, dt)) more = true;
            }
            if (Spring(ref _hubPop, ref _hubPopV, _hubHot ? 1 : 0, dt)) more = true;

            if (now - _openStart < OpenDur || now - _appearStart < AppearDur + Stagger * n) more = true;

            if (_closing)
            {
                if (now - _closeStart < CloseLength(n)) more = true;
                else if (!_closeDone)
                {
                    _closeDone = true;
                    more = true;
                }
                else
                {
                    _closing = false;
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => { if (Closed != null) Closed(); }));
                    return false;
                }
            }
            return more;
        }

        static bool Spring(ref double x, ref double v, double target, double dt)
        {
            for (double t = dt; t > 1e-6; t -= 1 / 240.0)
            {
                double h = Math.Min(t, 1 / 240.0);
                v += (Stiffness * (target - x) - Damping * v) * h;
                x += v * h;
            }
            if (Math.Abs(target - x) < 0.002 && Math.Abs(v) < 0.02)
            {
                x = target;
                v = 0;
                return false;
            }
            return true;
        }

        static double Clamp01(double v) { return v < 0 ? 0 : (v > 1 ? 1 : v); }
        static double EaseOut(double t) { return 1 - (1 - t) * (1 - t); }
        static double BackOut(double t)
        {
            const double c1 = 1.70158, c3 = c1 + 1;
            double u = t - 1;
            return 1 + c3 * u * u * u + c1 * u * u;
        }
        static int Mod(int a, int m) { return ((a % m) + m) % m; }

        void SliceMotion(int i, double now, out double scale, out double opacity)
        {
            double a = Clamp01((now - _appearStart - i * Stagger) / AppearDur);
            scale = 0.55 + 0.45 * BackOut(a);
            opacity = EaseOut(Clamp01(a * 1.6));
            if (_closing || _closeDone)
            {
                double f = _closeDone ? 1 : Clamp01((now - _closeStart - i * CloseStagger) / CloseSlice);
                scale *= 1 - 0.35 * f * f;
                opacity *= 1 - f * f;
            }
        }

        static double CloseLength(int n) { return Math.Max(CloseDur, CloseSlice + CloseStagger * Math.Max(0, n - 1)); }

        void WheelMotion(double now, out double scale, out double opacity, out double fade)
        {
            double o = Clamp01((now - _openStart) / OpenDur);
            scale = 0.82 + 0.18 * BackOut(o);
            opacity = fade = EaseOut(Clamp01((now - _openStart) / 0.12));
            if (_closing || _closeDone)
            {
                double p = _closeDone ? 1 : Clamp01((now - _closeStart) / CloseLength(SlotCount));
                scale *= 1 - 0.08 * p * p;
                fade *= 1 - p;
            }
        }

        readonly VisualCollection _visuals;
        readonly ContainerVisual _root = new ContainerVisual();
        readonly ScaleTransform _rootScale = new ScaleTransform();
        readonly DrawingVisual _shade = new DrawingVisual();
        readonly DrawingVisual _back = new DrawingVisual();
        readonly ContainerVisual _slotLayer = new ContainerVisual();
        readonly DrawingVisual _hub = new DrawingVisual(), _hubLit = new DrawingVisual();
        readonly DrawingVisual _ghost = new DrawingVisual();
        readonly TranslateTransform _ghostShift = new TranslateTransform();
        SlotVisual[] _slotVisuals = new SlotVisual[0];

        sealed class SlotVisual
        {
            public readonly ContainerVisual Root = new ContainerVisual();
            public readonly DrawingVisual Body = new DrawingVisual(), Lit = new DrawingVisual(), Marks = new DrawingVisual();
            public readonly ScaleTransform Scale = new ScaleTransform();
            public readonly TranslateTransform Shift = new TranslateTransform();
            public readonly RotateTransform Turn = new RotateTransform();

            public SlotVisual(Point center)
            {
                Scale.CenterX = Turn.CenterX = center.X;
                Scale.CenterY = Turn.CenterY = center.Y;
                var motion = new TransformGroup();
                motion.Children.Add(Scale);
                motion.Children.Add(Shift);
                motion.Children.Add(Turn);
                Root.Transform = motion;
                Body.CacheMode = new BitmapCache();
                Lit.CacheMode = new BitmapCache();
                Marks.CacheMode = new BitmapCache();
                Root.Children.Add(Body);
                Root.Children.Add(Lit);
                Root.Children.Add(Marks);
            }
        }

        protected override int VisualChildrenCount { get { return _visuals.Count; } }
        protected override Visual GetVisualChild(int index) { return _visuals[index]; }

        void BuildLayers()
        {
            _root.Transform = _rootScale;
            _shade.CacheMode = new BitmapCache();
            _back.CacheMode = new BitmapCache();
            _hub.CacheMode = new BitmapCache();
            _hubLit.CacheMode = new BitmapCache();
            _ghost.Transform = _ghostShift;
            _root.Children.Add(_shade);
            _root.Children.Add(_back);
            _root.Children.Add(_slotLayer);
            _root.Children.Add(_hub);
            _root.Children.Add(_hubLit);
            _root.Children.Add(_ghost);
            _visuals.Add(_root);
        }

        void RenderAll()
        {
            Point c = Center;
            int n = SlotCount;
            _rootScale.CenterX = c.X;
            _rootScale.CenterY = c.Y;
            RenderShade(c);
            _slotLayer.Children.Clear();
            if (_outgoing != null)
                foreach (SlotVisual sv in _outgoing) _slotLayer.Children.Add(sv.Root);
            _slotVisuals = new SlotVisual[n];
            for (int i = 0; i < n; i++)
            {
                _slotVisuals[i] = new SlotVisual(c);
                _slotLayer.Children.Add(_slotVisuals[i].Root);
                RenderSlot(i, n);
            }
            RenderState();
            ApplyMotion(Now());
        }

        void RenderState()
        {
            Point c = Center;
            int n = SlotCount;
            using (DrawingContext dc = _back.RenderOpen())
            {
                if (_editMode)
                {
                    if (_fan) dc.DrawGeometry(null, _dashPen, Arch(c, _outer + PopOut + 5));
                    else dc.DrawEllipse(null, _dashPen, c, _outer + PopOut + 5, _outer + PopOut + 5);
                }
                if (n > 0 && _slim && _plate)
                {
                    dc.DrawGeometry(_plateFill, null, _ring);
                    dc.DrawGeometry(_sheen, _plateRim, _ring);
                }
                if (n == 0) DrawEmptyRing(dc, c);
            }
            for (int i = 0; i < n && i < _slotVisuals.Length; i++) RenderMarks(i, n);
            using (DrawingContext dc = _hub.RenderOpen()) DrawHub(dc, c, n, false);
            using (DrawingContext dc = _hubLit.RenderOpen()) DrawHub(dc, c, n, true);
            using (DrawingContext dc = _ghost.RenderOpen())
            {
                if (_dragging && _dragFrom >= 0 && _dragFrom < _apps.Count)
                    DrawIcon(dc, _apps[_dragFrom], new Point(0, 0), IconSize(Math.Max(1, n)));
            }
        }

        void RenderShade(Point c)
        {
            using (DrawingContext dc = _shade.RenderOpen())
            {
                if (_fan)
                {
                    dc.PushClip(new RectangleGeometry(new Rect(0, 0, ViewWidth, c.Y + Gap)));
                    dc.DrawEllipse(HitFill, null, c, _outer + PopOut + 4, _outer + PopOut + 4);
                    dc.Pop();
                    var fade = new LinearGradientBrush { MappingMode = BrushMappingMode.Absolute, StartPoint = new Point(0, c.Y - 24), EndPoint = new Point(0, c.Y + 16) };
                    fade.GradientStops.Add(new GradientStop(Colors.Black, 0));
                    fade.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
                    dc.PushOpacityMask(fade);
                    dc.DrawEllipse(_shadow, null, new Point(c.X, c.Y + 5), _outer + PopOut + 12, _outer + PopOut + 12);
                    dc.Pop();
                }
                else
                {
                    dc.DrawEllipse(HitFill, null, c, _outer + PopOut + 4, _outer + PopOut + 4);
                    dc.DrawEllipse(_shadow, null, new Point(c.X, c.Y + 5), _outer + PopOut + 12, _outer + PopOut + 12);
                }
            }
        }

        void RenderSlot(int i, int n)
        {
            AppEntry app = _apps[Global(i)];
            Geometry g = _wedges[i];
            Point c = Center;
            SlotVisual sv = _slotVisuals[i];
            using (DrawingContext dc = sv.Body.RenderOpen())
            {
                dc.DrawGeometry(_base, null, g);
                if (_lift != null) dc.DrawGeometry(_lift, null, g);
                dc.DrawGeometry(_sheen, null, g);
                DrawSlotFace(dc, app, i, n, false);
            }
            using (DrawingContext dc = sv.Lit.RenderOpen())
            {
                Glow.DrawFill(dc, g);
                DrawSlotFace(dc, app, i, n, true);
            }
        }

        void DrawSlotFace(DrawingContext dc, AppEntry app, int i, int n, bool lit)
        {
            Point c = Center, ic = IconCenter(i, n);
            dc.PushTransform(new RotateTransform(SpokeAngle(i, n), ic.X, ic.Y));
            DrawIcon(dc, app, ic, IconSize(n));
            dc.Pop();
            if (_slim) DrawSpokeLabel(dc, app.Name, i, n, lit);
            else
            {
                double w = LabelWidth(n);
                DrawText(dc, app.Name, LabelSize(app.Name, w), FontWeights.Medium, lit ? Glow.InkBrush : White, ic.X, ic.Y + IconSize(n) / 2 + 5, w, 1, !lit);
                if (n > 1 && i < 9 && !_editMode)
                {
                    Point np = Polar(c, _outer - 11, SliceMid(i, n));
                    DrawText(dc, (i + 1).ToString(CultureInfo.InvariantCulture), 9, FontWeights.SemiBold, lit ? Glow.InkSoft : Faint, np.X, np.Y - 6.5, 16, 1, false);
                }
            }
        }

        void RenderMarks(int i, int n)
        {
            using (DrawingContext dc = _slotVisuals[i].Marks.RenderOpen())
            {
                if (i == _dropSlot && (_dragging || _externalDrag))
                    dc.DrawGeometry(null, _accentPen, _wedges[i]);
                if (_editMode && i == Active && !_dragging)
                {
                    Point b = BadgeCenter(i, n) - PopVector(i, n);
                    var badge = new EllipseGeometry(b, 9, 9);
                    if (_removeHot) Glow.DrawFill(dc, badge);
                    else dc.DrawGeometry(_badgeFill, null, badge);
                    Pen cross = _removeHot ? _badgeCrossLit : _badgeCross;
                    dc.DrawLine(cross, new Point(b.X - 3.2, b.Y - 3.2), new Point(b.X + 3.2, b.Y + 3.2));
                    dc.DrawLine(cross, new Point(b.X + 3.2, b.Y - 3.2), new Point(b.X - 3.2, b.Y + 3.2));
                }
            }
        }

        void ApplyMotion(double now)
        {
            double gs, go, gf;
            WheelMotion(now, out gs, out go, out gf);
            _rootScale.ScaleX = _rootScale.ScaleY = gs;
            _shade.Opacity = _back.Opacity = gf;
            double hubLit = Clamp01(_hubPop);
            _hub.Opacity = gf * (1 - hubLit);
            _hubLit.Opacity = gf * hubLit;
            _ghost.Opacity = 0.85 * gf;
            int n = Math.Min(SlotCount, _slotVisuals.Length);
            double turned = TurnProgress(now), turn = TurnAngle(turned, false);
            double turnIn = _fan || !Turning ? 1 : Clamp01(turned), turnOut = _fan ? 1 : Clamp01(1 - turned);
            for (int i = 0; i < n; i++)
            {
                double scale, opacity;
                SliceMotion(i, now, out scale, out opacity);
                if (_dragging && Global(i) == _dragFrom) opacity *= 0.35;
                opacity *= turnIn * HorizonFade(SliceMid(i, n) + turn, n);
                SlotVisual sv = _slotVisuals[i];
                sv.Scale.ScaleX = sv.Scale.ScaleY = scale;
                sv.Turn.Angle = turn * 180 / Math.PI;
                Vector off = PopVector(i, n);
                if (_popV[i] == 0)
                {
                    off = new Vector(Math.Round(off.X), Math.Round(off.Y));
                }
                sv.Shift.X = off.X;
                sv.Shift.Y = off.Y;
                double lit = Clamp01(_pop[i]);
                sv.Body.Opacity = opacity * go * (1 - lit);
                sv.Marks.Opacity = opacity * go;
                sv.Lit.Opacity = opacity * go * lit;
            }
            if (_outgoing != null)
            {
                int on = _outgoing.Length;
                double away = TurnAngle(turned, true), settle = 1 - EaseOut(Clamp01(turned * 2));
                for (int i = 0; i < on; i++)
                {
                    SlotVisual sv = _outgoing[i];
                    double pop = _outPop[i] * settle, opacity = gf * turnOut * HorizonFade(SliceMid(i, on) + away, on);
                    if (_dragging && _outFace * _perFace + i == _dragFrom) opacity *= 0.35;
                    Vector off = PopVector(i, on, pop);
                    sv.Shift.X = off.X;
                    sv.Shift.Y = off.Y;
                    sv.Turn.Angle = away * 180 / Math.PI;
                    sv.Body.Opacity = opacity * (1 - Clamp01(pop));
                    sv.Lit.Opacity = opacity * Clamp01(pop);
                    sv.Marks.Opacity = 0;
                }
            }
            _ghostShift.X = _mouse.X;
            _ghostShift.Y = _mouse.Y;
            if (!_drawn && go > 0.002)
            {
                _drawn = true;
                Log.Trace("first frame");
            }
        }

        void DrawSpokeLabel(DrawingContext dc, string name, int i, int n, bool lit)
        {
            double wIn, wOut;
            BladeWidths(n, out wIn, out wOut);
            double from = _inner + 8, to = OuterOf(i, n) - wOut - 2, length = to - from;
            if (length < 24 || string.IsNullOrEmpty(name)) return;
            double mid = SliceMid(i, n);
            Point p = Polar(Center, (from + to) / 2, mid);
            double degrees = SpokeAngle(i, n);
            double size = LabelSize(name, length);
            double height = Text(name, size, FontWeights.Medium, White, length, 1).Height;
            dc.PushTransform(new RotateTransform(degrees, p.X, p.Y));
            DrawText(dc, name, size, FontWeights.Medium, lit ? Glow.InkBrush : White, p.X, p.Y - height / 2, length, 1, !lit);
            dc.Pop();
        }

        void DrawEmptyRing(DrawingContext dc, Point c)
        {
            bool hot = _ringHot || _externalDrag;
            if (hot) Glow.DrawFill(dc, _ring);
            else
            {
                dc.DrawGeometry(_base, null, _ring);
                dc.DrawGeometry(_sheen, null, _ring);
            }
            double mid = (_inner + _outer) / 2;
            DrawText(dc, "Click to add apps", 12.5, FontWeights.SemiBold, hot ? Glow.InkBrush : White, c.X, c.Y - mid - 8, 160, 1, !hot);
            DrawText(dc, "or drop them here", 11, FontWeights.Normal, hot ? Glow.InkSoft : Soft, c.X, _fan ? c.Y - mid + 9 : c.Y + mid - 8, 160, 1, !hot);
        }

        void DrawHub(DrawingContext dc, Point c, int n, bool hot)
        {
            double hr = _inner - Gap;
            Geometry hub = new EllipseGeometry(c, hr, hr);
            if (_fan) hub = Dome(c, hr, 16);
            if (hot) Glow.DrawFill(dc, hub);
            else
            {
                dc.DrawGeometry(_hubBase, null, hub);
                dc.DrawGeometry(_sheen, null, hub);
            }
            Brush ink = hot ? Glow.InkBrush : White, inkSoft = hot ? Glow.InkSoft : Soft, inkFaint = hot ? Glow.InkSoft : Faint;

            double w = hr * (_fan ? 1.45 : 1.7);
            string title, sub;
            double titleSize = 14.5;
            if (hot) { title = _editMode ? "Done" : "Close"; sub = _editMode ? "Stop editing" : "Esc"; }
            else if (n == 0) { title = "No apps"; sub = "Right-click to add"; }
            else if (_editMode) { title = "Editing"; sub = "Drop apps on the ring"; }
            else
            {
                DateTime t = DateTime.Now;
                title = t.ToString("t", CultureInfo.CurrentCulture);
                sub = t.ToString("ddd d MMM", CultureInfo.CurrentCulture);
                titleSize = _fan ? Math.Min(34, hr * 0.27) : 21;
            }
            FormattedText tt = Text(title, titleSize, FontWeights.SemiBold, White, w, 2);
            double block = tt.Height + 4 + 14;
            double middle = _fan ? c.Y - hr * 0.44 : c.Y;
            double top = middle - block / 2 - (Faces > 1 && !_fan ? 4 : 0);
            DrawText(dc, title, titleSize, FontWeights.SemiBold, ink, c.X, top, w, 2, !hot);
            DrawText(dc, sub, 11, FontWeights.Normal, inkSoft, c.X, top + tt.Height + 4, w, 1, !hot);

            int faces = Faces;
            if (faces > 1)
            {
                int shown = Mod(_face + _queued, faces);
                double y = _fan ? c.Y - 10 : c.Y + hr * 0.62, x0 = c.X - (faces - 1) * 4.5;
                for (int f = 0; f < faces; f++)
                    dc.DrawEllipse(f == shown ? ink : inkFaint, null, new Point(x0 + f * 9, y), 2.6, 2.6);
            }
        }

        void DrawIcon(DrawingContext dc, AppEntry app, Point center, double size)
        {
            ImageSource img = _icons != null ? _icons.Get(app.IconKey, IconPixels) : null;
            var rect = new Rect(center.X - size / 2, center.Y - size / 2, size, size);
            if (img != null)
            {
                dc.DrawImage(img, rect);
                return;
            }
            double r = size * 0.26;
            dc.DrawRoundedRectangle(TileBrush(app.Name), _tileRim, rect, r, r);
            string letter = string.IsNullOrEmpty(app.Name) ? "?" : app.Name.Substring(0, 1).ToUpperInvariant();
            DrawText(dc, letter, size * 0.46, FontWeights.SemiBold, White, center.X, center.Y - size * 0.33, size, 1);
        }

        Brush TileBrush(string name)
        {
            name = name ?? "";
            Brush b;
            if (_tiles.TryGetValue(name, out b)) return b;
            int hash = 17;
            foreach (char ch in name) hash = hash * 31 + ch;
            double hue = Math.Abs(hash % 360);
            var g = new LinearGradientBrush(Hsv(hue, 0.55, 0.95), Hsv((hue + 40) % 360, 0.65, 0.70), 90);
            _tiles[name] = b = Frozen(g);
            return b;
        }

        static Color Hsv(double h, double s, double v)
        {
            double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c, r, g, b;
            if (h < 60) { r = c; g = x; b = 0; }
            else if (h < 120) { r = x; g = c; b = 0; }
            else if (h < 180) { r = 0; g = c; b = x; }
            else if (h < 240) { r = 0; g = x; b = c; }
            else if (h < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }
            return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
        }

        FormattedText Text(string s, double size, FontWeight weight, Brush brush, double maxWidth, int lines)
        {
            string key = s + "\u0001" + size + "\u0001" + weight + "\u0001" + brush.GetHashCode() + "\u0001" + maxWidth + "\u0001" + lines;
            FormattedText ft;
            if (_texts.TryGetValue(key, out ft)) return ft;
            ft = new FormattedText(s ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface(_family, FontStyles.Normal, weight, FontStretches.Normal), size, brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            ft.MaxTextWidth = Math.Max(8, maxWidth);
            ft.MaxLineCount = lines;
            ft.Trimming = TextTrimming.CharacterEllipsis;
            ft.TextAlignment = TextAlignment.Center;
            if (_texts.Count > 400) _texts.Clear();
            _texts[key] = ft;
            return ft;
        }

        void DrawText(DrawingContext dc, string s, double size, FontWeight weight, Brush brush, double cx, double top, double maxWidth, int lines, bool shadow = true)
        {
            if (string.IsNullOrEmpty(s)) return;
            double left = cx - maxWidth / 2;
            if (shadow) dc.DrawText(Text(s, size, weight, TextShadow, maxWidth, lines), new Point(left, top + 1));
            dc.DrawText(Text(s, size, weight, brush, maxWidth, lines), new Point(left, top));
        }

        static T Frozen<T>(T f) where T : Freezable
        {
            f.Freeze();
            return f;
        }

        void Track(DonutHit h)
        {
            int hover = h.OnSlice ? h.Slot : -1;
            bool hub = h.Kind == HitKind.Center, rem = h.Kind == HitKind.Remove, ring = h.Kind == HitKind.EmptyRing;
            if (hover != _hover || hub != _hubHot || rem != _removeHot || ring != _ringHot)
            {
                _hover = hover;
                _hubHot = hub;
                _removeHot = rem;
                _ringHot = ring;
                if (hover >= 0) _key = -1;
                Animate();
            }
            Cursor = hover >= 0 || hub || ring ? Cursors.Hand : null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            _mouse = e.GetPosition(this);
            if (_closing) return;
            if (_press.OnSlice && e.LeftButton == MouseButtonState.Pressed)
            {
                if (!_dragging && (_mouse - _pressPoint).Length > 7)
                {
                    _dragging = true;
                    _dragFrom = Global(_press.Slot);
                    _key = -1;
                }
                if (_dragging)
                {
                    DragTo(_mouse);
                    Animate();
                    return;
                }
            }
            Track(HitTest(_mouse));
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            if (!_dragging && !_closing) Track(new DonutHit(HitKind.Outside, -1));
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_closing) return;
            Focus();
            _pressPoint = e.GetPosition(this);
            _press = HitTest(_pressPoint);
            CaptureMouse();
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (!IsMouseCaptured) return;
            DonutHit down = _press;
            bool dragged = _dragging;
            int from = _dragFrom, to = _dropSlot >= 0 ? Global(_dropSlot) : -1;
            ReleaseMouseCapture();
            _press = new DonutHit(HitKind.Outside, -1);
            _dragging = false;
            _dragFrom = _dropSlot = -1;
            Point p = e.GetPosition(this);
            if (dragged)
            {
                if (to >= 0 && to != from && Reordered != null) Reordered(from, to);
                Track(HitTest(p));
                Animate();
                return;
            }
            if (_closing) return;
            DonutHit up = HitTest(p);
            if (up.Kind != down.Kind || up.Slot != down.Slot) return;
            switch (up.Kind)
            {
                case HitKind.Slice: if (Invoked != null) Invoked(Global(up.Slot)); break;
                case HitKind.Remove: if (RemoveRequested != null) RemoveRequested(Global(up.Slot)); break;
                case HitKind.Center: if (CenterInvoked != null) CenterInvoked(); break;
                case HitKind.EmptyRing: if (AddRequested != null) AddRequested(); break;
                case HitKind.Outside:
                    if (!Turning && DismissRequested != null) DismissRequested();
                    break;
            }
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                _dragFrom = _dropSlot = -1;
                Animate();
            }
            _press = new DonutHit(HitKind.Outside, -1);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.ChangedButton != MouseButton.Middle || _closing || _dragging) return;
            e.Handled = true;
            DonutHit h = HitTest(e.GetPosition(this));
            if (h.OnSlice && MiddleInvoked != null) MiddleInvoked(Global(h.Slot));
        }

        protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_closing || _dragging) return;
            DonutHit h = HitTest(e.GetPosition(this));
            if (h.Kind == HitKind.Outside) { if (DismissRequested != null) DismissRequested(); }
            else if (ContextRequested != null) ContextRequested(h.OnSlice ? Global(h.Slot) : -1);
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            e.Handled = true;
            double now = Now();
            bool newTurn = now - _lastWheel > WheelQuiet;
            _lastWheel = now;
            if (newTurn) Flip(e.Delta < 0 ? 1 : -1);
        }

        public bool HandleKey(Key key, ModifierKeys modifiers)
        {
            if (_closing) return false;
            int n = SlotCount, active = Active;
            switch (key)
            {
                case Key.Escape:
                    if (DismissRequested != null) DismissRequested();
                    return true;
                case Key.Tab:
                    Flip((modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                    return true;
                case Key.PageDown: Flip(1); return true;
                case Key.PageUp: Flip(-1); return true;
                case Key.Right:
                case Key.Down:
                    MoveKey(1, n);
                    return true;
                case Key.Left:
                case Key.Up:
                    MoveKey(-1, n);
                    return true;
                case Key.Enter:
                case Key.Space:
                    if (active >= 0 && active < n) { if (Invoked != null) Invoked(Global(active)); }
                    else if (n == 0 && AddRequested != null) AddRequested();
                    return true;
                case Key.Delete:
                    if (_editMode && active >= 0 && active < n && RemoveRequested != null) RemoveRequested(Global(active));
                    return true;
                case Key.F2:
                    if (active >= 0 && active < n && RenameRequested != null) RenameRequested(Global(active));
                    return true;
                case Key.Apps:
                    if (ContextRequested != null) ContextRequested(active >= 0 && active < n ? Global(active) : -1);
                    return true;
            }
            int digit = -1;
            if (key >= Key.D1 && key <= Key.D9) digit = key - Key.D1;
            else if (key >= Key.NumPad1 && key <= Key.NumPad9) digit = key - Key.NumPad1;
            if (digit >= 0 && digit < n)
            {
                if (Invoked != null) Invoked(Global(digit));
                return true;
            }
            return false;
        }

        void MoveKey(int step, int n)
        {
            if (n == 0) return;
            int from = Active >= 0 ? Active : (step > 0 ? -1 : 0);
            _key = Mod(from + step, n);
            _hover = -1;
            Animate();
        }

        static string[] DroppedItems(IDataObject data)
        {
            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = data.GetData(DataFormats.FileDrop) as string[];
                if (files != null) return files;
            }
            if (data.GetDataPresent(DataFormats.UnicodeText))
            {
                string text = (data.GetData(DataFormats.UnicodeText) as string ?? "").Trim();
                Uri uri;
                if (Uri.TryCreate(text, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    return new[] { text };
            }
            return new string[0];
        }

        protected override void OnDragEnter(DragEventArgs e) { OnDragOver(e); }

        protected override void OnDragOver(DragEventArgs e)
        {
            e.Handled = true;
            if (DroppedItems(e.Data).Length == 0)
            {
                e.Effects = DragDropEffects.None;
                return;
            }
            e.Effects = (e.AllowedEffects & DragDropEffects.Link) != 0 ? DragDropEffects.Link : DragDropEffects.Copy;
            DonutHit h = HitTest(e.GetPosition(this));
            int slot = h.OnSlice ? h.Slot : -1;
            if (!_externalDrag || slot != _dropSlot)
            {
                _externalDrag = true;
                _dropSlot = slot;
                Animate();
            }
        }

        protected override void OnDragLeave(DragEventArgs e)
        {
            _externalDrag = false;
            _dropSlot = -1;
            Animate();
        }

        protected override void OnDrop(DragEventArgs e)
        {
            e.Handled = true;
            string[] items = DroppedItems(e.Data);
            int slot = _dropSlot;
            _externalDrag = false;
            _dropSlot = -1;
            Animate();
            if (items.Length > 0 && ItemsDropped != null)
                ItemsDropped(items, slot >= 0 ? Global(slot) : _apps.Count);
        }
    }
}
