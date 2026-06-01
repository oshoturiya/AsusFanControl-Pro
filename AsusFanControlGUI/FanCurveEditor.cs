using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using System.Text;

namespace AsusFanControlGUI
{
    public class FanCurveEditor : UserControl
    {
        private Chart chart;
        private Series fanCurveSeries;
        private Series currentTempSeries;
        private DataPoint selectedPoint = null;
        public bool IsDragging { get; private set; } = false;

        public FanCurveEditor()
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
            
            // X Axis
            area.AxisX.Title = "Temperature (°C)";
            area.AxisX.TitleForeColor = Color.Silver;
            area.AxisX.LabelStyle.ForeColor = Color.Silver;
            area.AxisX.Minimum = 0;
            area.AxisX.Maximum = 100;
            area.AxisX.Interval = 10;
            area.AxisX.LineColor = Color.FromArgb(64, 64, 64);
            area.AxisX.MajorGrid.LineColor = Color.FromArgb(50, 50, 50);
            area.AxisX.MajorGrid.LineDashStyle = ChartDashStyle.Dash;

            // Y Axis
            area.AxisY.Title = "Fan Speed (%)";
            area.AxisY.TitleForeColor = Color.Silver;
            area.AxisY.LabelStyle.ForeColor = Color.Silver;
            area.AxisY.Minimum = 0;
            area.AxisY.Maximum = 100;
            area.AxisY.Interval = 10;
            area.AxisY.LineColor = Color.FromArgb(64, 64, 64);
            area.AxisY.MajorGrid.LineColor = Color.FromArgb(50, 50, 50);
            area.AxisY.MajorGrid.LineDashStyle = ChartDashStyle.Dash;

            chart.ChartAreas.Add(area);

            // Fan Curve Line
            fanCurveSeries = new Series("FanCurve");
            fanCurveSeries.ChartType = SeriesChartType.Line;
            fanCurveSeries.Color = Color.FromArgb(0, 255, 255);
            fanCurveSeries.BorderWidth = 2;
            fanCurveSeries.MarkerStyle = MarkerStyle.Circle;
            fanCurveSeries.MarkerSize = 12;
            fanCurveSeries.MarkerColor = Color.White;
            fanCurveSeries.MarkerBorderColor = Color.FromArgb(0, 255, 255);
            fanCurveSeries.MarkerBorderWidth = 2;
            
            // Default 7 Points
            fanCurveSeries.Points.AddXY(0, 0);    
            fanCurveSeries.Points.AddXY(30, 20);  
            fanCurveSeries.Points.AddXY(45, 35);  
            fanCurveSeries.Points.AddXY(60, 50);  
            fanCurveSeries.Points.AddXY(70, 70);  
            fanCurveSeries.Points.AddXY(85, 90);  
            fanCurveSeries.Points.AddXY(100, 100);

            chart.Series.Add(fanCurveSeries);

            // Indicator
            currentTempSeries = new Series("CurrentTemp");
            currentTempSeries.ChartType = SeriesChartType.Point;
            currentTempSeries.Color = Color.Red;
            currentTempSeries.MarkerSize = 14;
            currentTempSeries.MarkerStyle = MarkerStyle.Cross;
            currentTempSeries.Points.AddXY(0, 0);
            
            chart.Series.Add(currentTempSeries);

            // Events
            chart.MouseDown += Chart_MouseDown;
            chart.MouseMove += Chart_MouseMove;
            chart.MouseUp += Chart_MouseUp;
            chart.GetToolTipText += Chart_GetToolTipText;
        }

        private void Chart_GetToolTipText(object sender, ToolTipEventArgs e)
        {
            if (e.HitTestResult.ChartElementType == ChartElementType.DataPoint && 
                e.HitTestResult.Series == fanCurveSeries)
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
            if (result.ChartElementType == ChartElementType.DataPoint && result.Series == fanCurveSeries)
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
                    
                    yValue = Math.Max(0, Math.Min(100, yValue));
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
            var sortedPoints = fanCurveSeries.Points.OrderBy(p => p.XValue).ToList();
            fanCurveSeries.Points.Clear();
            foreach (var p in sortedPoints) fanCurveSeries.Points.Add(p);
        }

        public int UpdateAndGetSpeed(int currentTemp)
        {
            currentTempSeries.Points[0].XValue = currentTemp;
            int targetSpeed = CalculateFanSpeed(currentTemp);
            currentTempSeries.Points[0].YValues[0] = targetSpeed;
            chart.Invalidate();
            return targetSpeed;
        }

        public int CalculateFanSpeed(double currentTemp)
        {
            var points = fanCurveSeries.Points;
            
            if (currentTemp <= points[0].XValue) return (int)points[0].YValues[0];
            if (currentTemp >= points[points.Count - 1].XValue) return (int)points[points.Count - 1].YValues[0];

            for (int i = 0; i < points.Count - 1; i++)
            {
                DataPoint p1 = points[i];
                DataPoint p2 = points[i + 1];
                if (currentTemp >= p1.XValue && currentTemp < p2.XValue)
                {
                    double slope = (p2.YValues[0] - p1.YValues[0]) / (p2.XValue - p1.XValue);
                    return (int)(p1.YValues[0] + (currentTemp - p1.XValue) * slope);
                }
            }
            return 0;
        }

        public string GetPointsString()
        {
            StringBuilder sb = new StringBuilder();
            foreach (var p in fanCurveSeries.Points)
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
                    fanCurveSeries.Points.Clear();
                    foreach (var p in newPoints.Points) fanCurveSeries.Points.Add(p);
                    SortPoints();
                    chart.Invalidate();
                }
            }
            catch { }
        }
    }
}