// AI-HANDOFF: FrequencyCurveEditor C# Class
// PURPOSE: This UserControl renders an interactive chart using standard MS Chart Controls.
//   It allows Osho to drag control points in any direction (swapping load/frequency and adjusting thresholds freely),
//   behaving identically to his premium FanCurveEditor class.
// ALGORITHM & RESPONSIVENESS:
//   - X-Axis represents CPU Load (%) from 0 to 100.
//   - Y-Axis represents Target CPU Frequency (GHz) from 0.8 to 4.1.
//   - Clicking and dragging moves dots in both dimensions, automatically calling SortPoints() to keep the list
//     ordered by CPU Load.
//   - Includes a real-time red crosshairs indicator showing the current live CPU Load and applied clock limit.

using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using System.Text;

namespace AsusFanControlGUI
{
    public class FrequencyCurveEditor : UserControl
    {
        private Chart chart;
        private Series freqCurveSeries;
        private Series currentLoadSeries;
        private DataPoint selectedPoint = null;
        public bool IsDragging { get; private set; } = false;

        public FrequencyCurveEditor()
        {
            InitializeChart();
        }

        private void InitializeChart()
        {
            this.chart = new Chart();
            this.chart.Dock = DockStyle.Fill;
            this.chart.BackColor = Color.FromArgb(20, 20, 20);
            this.Controls.Add(this.chart);

            ChartArea area = new ChartArea("MainArea");
            area.BackColor = Color.FromArgb(30, 30, 30);
            
            // X Axis (CPU Load)
            area.AxisX.Title = "CPU Load (%)";
            area.AxisX.TitleForeColor = Color.Silver;
            area.AxisX.LabelStyle.ForeColor = Color.Silver;
            area.AxisX.Minimum = 0;
            area.AxisX.Maximum = 100;
            area.AxisX.Interval = 10;
            area.AxisX.LineColor = Color.FromArgb(64, 64, 64);
            area.AxisX.MajorGrid.LineColor = Color.FromArgb(50, 50, 50);
            area.AxisX.MajorGrid.LineDashStyle = ChartDashStyle.Dash;

            // Y Axis (Target Frequency GHz)
            area.AxisY.Title = "Target Frequency (GHz)";
            area.AxisY.TitleForeColor = Color.Silver;
            area.AxisY.LabelStyle.ForeColor = Color.Silver;
            area.AxisY.Minimum = 0.8;
            area.AxisY.Maximum = 4.1;
            area.AxisY.Interval = 0.5;
            area.AxisY.LineColor = Color.FromArgb(64, 64, 64);
            area.AxisY.MajorGrid.LineColor = Color.FromArgb(50, 50, 50);
            area.AxisY.MajorGrid.LineDashStyle = ChartDashStyle.Dash;

            chart.ChartAreas.Add(area);

            // Frequency Curve Line
            freqCurveSeries = new Series("FreqCurve");
            freqCurveSeries.ChartType = SeriesChartType.Line;
            freqCurveSeries.Color = Color.FromArgb(0, 255, 255);
            freqCurveSeries.BorderWidth = 2;
            freqCurveSeries.MarkerStyle = MarkerStyle.Circle;
            freqCurveSeries.MarkerSize = 12;
            freqCurveSeries.MarkerColor = Color.White;
            freqCurveSeries.MarkerBorderColor = Color.FromArgb(0, 255, 255);
            freqCurveSeries.MarkerBorderWidth = 2;
            
            // Default 5 Points
            freqCurveSeries.Points.AddXY(0, 1.2);    
            freqCurveSeries.Points.AddXY(25, 2.0);  
            freqCurveSeries.Points.AddXY(50, 2.8);  
            freqCurveSeries.Points.AddXY(75, 3.6);  
            freqCurveSeries.Points.AddXY(100, 4.1);  

            chart.Series.Add(freqCurveSeries);

            // Real-Time Indicator
            currentLoadSeries = new Series("CurrentLoad");
            currentLoadSeries.ChartType = SeriesChartType.Point;
            currentLoadSeries.Color = Color.Red;
            currentLoadSeries.MarkerSize = 14;
            currentLoadSeries.MarkerStyle = MarkerStyle.Cross;
            currentLoadSeries.Points.AddXY(0, 1.2);
            
            chart.Series.Add(currentLoadSeries);

            // Events mapping
            chart.MouseDown += Chart_MouseDown;
            chart.MouseMove += Chart_MouseMove;
            chart.MouseUp += Chart_MouseUp;
            chart.GetToolTipText += Chart_GetToolTipText;
        }

        private void Chart_GetToolTipText(object sender, ToolTipEventArgs e)
        {
            if (e.HitTestResult.ChartElementType == ChartElementType.DataPoint && 
                e.HitTestResult.Series == freqCurveSeries)
            {
                this.Cursor = Cursors.Hand;
            }
            else
            {
                this.Cursor = Cursors.Default;
            }
        }

        private void Chart_MouseDown(object sender, MouseEventArgs e)
        {
            HitTestResult result = chart.HitTest(e.X, e.Y);
            if (result.ChartElementType == ChartElementType.DataPoint && result.Series == freqCurveSeries)
            {
                selectedPoint = result.Series.Points[result.PointIndex];
                IsDragging = true;
            }
        }

        private void Chart_MouseMove(object sender, MouseEventArgs e)
        {
            if (IsDragging && selectedPoint != null)
            {
                ChartArea area = chart.ChartAreas[0];
                try
                {
                    double yValue = area.AxisY.PixelPositionToValue(e.Y);
                    double xValue = area.AxisX.PixelPositionToValue(e.X);
                    
                    // Constrain frequency (Y) and load (X)
                    yValue = Math.Max(0.8, Math.Min(4.1, yValue));
                    xValue = Math.Max(0, Math.Min(100, xValue));

                    selectedPoint.YValues[0] = yValue;
                    selectedPoint.XValue = xValue;
                    
                    SortPoints();
                    chart.Invalidate();
                }
                catch { }
            }
        }

        private void Chart_MouseUp(object sender, MouseEventArgs e)
        {
            IsDragging = false;
            selectedPoint = null;
            SortPoints();
        }

        private void SortPoints()
        {
            var sortedPoints = freqCurveSeries.Points.OrderBy(p => p.XValue).ToList();
            freqCurveSeries.Points.Clear();
            foreach (var p in sortedPoints) freqCurveSeries.Points.Add(p);
        }

        public double UpdateAndGetLimit(double currentLoad)
        {
            currentLoadSeries.Points[0].XValue = currentLoad;
            double targetLimit = CalculateFrequencyLimit(currentLoad);
            currentLoadSeries.Points[0].YValues[0] = targetLimit;
            chart.Invalidate();
            return targetLimit;
        }

        public double CalculateFrequencyLimit(double currentLoad)
        {
            var points = freqCurveSeries.Points;
            
            if (currentLoad <= points[0].XValue) return points[0].YValues[0];
            if (currentLoad >= points[points.Count - 1].XValue) return points[points.Count - 1].YValues[0];

            for (int i = 0; i < points.Count - 1; i++)
            {
                DataPoint p1 = points[i];
                DataPoint p2 = points[i + 1];
                if (currentLoad >= p1.XValue && currentLoad < p2.XValue)
                {
                    double slope = (p2.YValues[0] - p1.YValues[0]) / (p2.XValue - p1.XValue);
                    return p1.YValues[0] + (currentLoad - p1.XValue) * slope;
                }
            }
            return points[points.Count - 1].YValues[0];
        }

        public string GetPointsString()
        {
            StringBuilder sb = new StringBuilder();
            foreach (var p in freqCurveSeries.Points)
            {
                sb.Append($"{p.XValue},{p.YValues[0]};");
            }
            return sb.ToString().TrimEnd(';');
        }

        public void SetPointsFromString(string data)
        {
            if (string.IsNullOrWhiteSpace(data)) return;
            try
            {
                var newPoints = new Series();
                string[] pointPairs = data.Split(';');
                foreach (var pair in pointPairs)
                {
                    string[] coords = pair.Split(',');
                    if (coords.Length == 2)
                    {
                        double x = double.Parse(coords[0]);
                        double y = double.Parse(coords[1]);
                        newPoints.Points.AddXY(x, y);
                    }
                }
                if (newPoints.Points.Count > 1)
                {
                    freqCurveSeries.Points.Clear();
                    foreach (var p in newPoints.Points) freqCurveSeries.Points.Add(p);
                    SortPoints();
                    chart.Invalidate();
                }
            }
            catch { }
        }
    }
}
