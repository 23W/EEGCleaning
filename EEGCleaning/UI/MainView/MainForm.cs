using EEGCleaning.Model;
using EEGCleaning.UI.MainView;
using EEGCleaning.UI.MainView.StateMachine;
using EEGCleaning.Utilities;
using EEGCore.Data;
using EEGCore.Processing;
using EEGCore.Processing.Analysis;
using EEGCore.Processing.ICA;
using EEGCore.Utilities;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;

namespace EEGCleaning
{
    public partial class MainForm : Form
    {
        #region Properties

        internal RecordViewModel ViewModel { get; set; } = new RecordViewModel();

        internal StateMachine StateMachine { get; init; }

        internal RecordPlotModel PlotModel => (RecordPlotModel)m_plotView.Model;
        internal TimeSpanAxis? PlotModelXAxis => (TimeSpanAxis?)PlotModel.Axes.FirstOrDefault(a => a.IsHorizontal() && a is TimeSpanAxis);
        internal IEnumerable<LinearAxis> PlotModelYAxes => PlotModel.Axes.Where(a => a.IsVertical() && a is LinearAxis && a.Tag is Lead).Cast<LinearAxis>();

        internal Point LastPoint { get; set; } = Point.Empty;

        internal Button ICAControl => m_icaButton;
        internal ToolStripItem StandardICAControl => m_standradICAToolStripMenuItem;
        internal ToolStripItem NormalizedICAControl => m_normalizedICAToolStripMenuItem;
        internal Button ICAComposeControl => m_icaComposeButton;
        internal SaveFileDialog SaveFileDialog => m_saveFileDialog;

        #endregion

        #region Internal Properties

        OxyImage ArtifactImage { get; init; }
        bool NeedPlotRescale { get; set; } = false;
        bool InPlotRescaleExecution { get; set; } = false;
        bool InFilterChangeExecution { get; set; } = false;

        SpeedItem[] SpeedItems => new[]
        {
            new SpeedItem() { Value = -1 },
            new SpeedItem() { Value = 7.5 },
            new SpeedItem() { Value = 15 },
            new SpeedItem() { Value = 30 },
            new SpeedItem() { Value = 60 },
            new SpeedItem() { Value = 120 },
        };

        AmplItem[] AmplItems => new[]
        {
            new AmplItem() { Value = -1 },
            new AmplItem() { Value = 10 },
            new AmplItem() { Value = 25 },
            new AmplItem() { Value = 50 },
            new AmplItem() { Value = 100 },
            new AmplItem() { Value = 250 },
            new AmplItem() { Value = 500 },
            new AmplItem() { Value = 1000 },
            new AmplItem() { Value = 1500 },
            new AmplItem() { Value = 2000 },
        };

        FrequencyItem[] FrequencyItems => new[]
        {
            new FrequencyItem() { Value = -1 },
            new FrequencyItem() { Value = 0.1 },
            new FrequencyItem() { Value = 0.2 },
            new FrequencyItem() { Value = 0.3 },
            new FrequencyItem() { Value = 0.5 },
            new FrequencyItem() { Value = 1 },
            new FrequencyItem() { Value = 2 },
            new FrequencyItem() { Value = 3 },
            new FrequencyItem() { Value = 4 },
            new FrequencyItem() { Value = 5 },
            new FrequencyItem() { Value = 6 },
            new FrequencyItem() { Value = 7 },
            new FrequencyItem() { Value = 8 },
            new FrequencyItem() { Value = 9 },
            new FrequencyItem() { Value = 10 },
            new FrequencyItem() { Value = 13 },
            new FrequencyItem() { Value = 15 },
            new FrequencyItem() { Value = 20 },
            new FrequencyItem() { Value = 25 },
            new FrequencyItem() { Value = 35 },
            new FrequencyItem() { Value = 50 },
            new FrequencyItem() { Value = 100 },
        };

        #endregion


        #region Nested classes

        internal class RecordPlotModel : PlotModel
        {
            internal event EventHandler? BeforeRendering;
            internal event EventHandler? AfterRendering;

            IRenderContext? CurrentRenderContext { get; set; } = default;
            IDisposable? ClipToken { get; set; } = default;

            internal void LockRenderingContext()
            {
                ClipToken = CurrentRenderContext?.AutoResetClip(new OxyRect());
            }

            internal void UnlockRenderingContext()
            {
                ClipToken?.Dispose();
                ClipToken = default;
                CurrentRenderContext = default;
            }

            protected override void RenderOverride(IRenderContext rc, OxyRect rect)
            {
                CurrentRenderContext = rc;
                BeforeRendering?.Invoke(this, EventArgs.Empty);

                base.RenderOverride(rc, rect);

                AfterRendering?.Invoke(this, EventArgs.Empty);
            }
        }

        #endregion

        public MainForm()
        {
            InitializeComponent();

            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream("EEGCleaning.Resources.Artifact.png"))
            {
                ArtifactImage = new OxyImage(stream);
            }

            m_speedComboBox.Items.AddRange(SpeedItems);
            m_amplComboBox.Items.AddRange(AmplItems);
            m_filterLowCutOffComboBox.Items.AddRange(FrequencyItems);
            m_filterHighCutOffComboBox.Items.AddRange(FrequencyItems);

            StateMachine = new StateMachine(this);
        }

        internal void UpdatePlot()
        {
            UpdatePlot(ViewModel.ViewMode);
        }

        internal object GetViewModelObject(object? sender)
        {
            var res = PlotModel as object;

            if (sender is Record record)
            {
                res = record;
            }
            else if (sender is Lead lead)
            {
                res = lead;
            }
            else if (sender is RecordRange range)
            {
                res = range;
            }
            else if ((sender is PlotModel) ||
                     (sender is PlotController))
            {
                res = ViewModel.VisibleRecord;
            }
            else if (sender is PlotElement element)
            {
                res = element.Tag;
            }

            return res;
        }

        internal bool IsPanDistance(double time1, double time2)
        {
            bool res = Math.Abs(time2 - time1) * ViewModel.VisibleRecord.SampleRate > 10;
            return res;
        }

        internal void RunICADecompose(RecordRange? range = default, bool useVisibleRecord = true, bool normalizePower = false, bool analyzeComponents = true)
        {
            using (var progressForm = new ProgressForm())
            {
                progressForm.Start += async form =>
                {
                    await Task.Run(() => DoICADecompose(range, useVisibleRecord, normalizePower, analyzeComponents));
                    form.DialogResult = DialogResult.OK;
                };

                progressForm.ShowDialog();
            }
        }

        void DoICADecompose(RecordRange? range, bool useVisibleRecord, bool normalizePower, bool analyzeComponents)
        {
            var ica = new FastICA()
            {
                MaxIterationCount = 10000,
                Tolerance = 1E-06,
                NormalizePower = normalizePower,
            };
            var icaMixture = useVisibleRecord ? ViewModel.VisibleRecord : ViewModel.ProcessedRecord;

            ViewModel.IndependentComponents = ica.Decompose(icaMixture, range);

            if (analyzeComponents)
            {
                foreach (var analyzer in BuildICAAnalyzers(ViewModel.IndependentComponents))
                {
                    analyzer.Analyze();
                }

                foreach (var (lead, componentIndex) in ViewModel.IndependentComponents.Leads.Cast<ComponentLead>().WithIndex())
                {
                    if (lead.IsReferenceElectrodeArtifact ||
                        lead.IsSingleElectrodeArtifact)
                    {
                        lead.Suppress = SuppressType.ZeroLead;
                    }
                    else if (lead.IsEyeArtifact)
                    {
                        lead.Suppress = SuppressType.HiPass10;
                    }
                    else
                    {
                        lead.Suppress = SuppressType.None;
                    }

                    // Build suppress alternative
                    ViewModel.IndependentComponents.BuildLeadAlternativeSuppress(componentIndex);
                }
            }

#if DEBUG
            var currentPath = Directory.GetCurrentDirectory();
            File.WriteAllText(Path.Combine(currentPath, "ICA.json"), ViewModel.IndependentComponents.ToJson());

            foreach (var (lead, componentIndex) in ViewModel.IndependentComponents.Leads.Cast<ComponentLead>().WithIndex())
            {
                var componentWeights = ViewModel.IndependentComponents.GetMixingVector(componentIndex);
                File.WriteAllText(Path.Combine(currentPath, $"ICA-{lead.Name}.json"), JsonSerializer.Serialize(componentWeights));
            }
#endif

        }

        internal void RunICACompose(bool useVisibleRecord = true)
        {
            var ica = new FastICA();
            var icaComponents = useVisibleRecord ? (ICARecord)ViewModel.VisibleRecord : ViewModel.IndependentComponents;

            ViewModel.ProcessedRecord = ica.Compose(icaComponents, SuppressComponents.MatrixAndComponents);
        }

        internal void UpdatePlot(ModelViewMode viewMode)
        {
            NeedPlotRescale = true;

            var oldViewMode = ViewModel.ViewMode;
            ViewModel.ViewMode = viewMode;

            if (oldViewMode != viewMode)
            {
                ViewModel.ResetVisibleRecord();
                ViewModel.HiddenLeadNames.Clear();
                ViewModel.Position = TimePositionItem.Default;
                ViewModel.Amplitude = AmplItem.Default;
            }

            UnsubsribePlotEvents(m_plotView.Model);

            var plotModel = new RecordPlotModel();
            var plotWeightsModel = new PlotModel();
            switch (ViewModel.ViewMode)
            {
                case ModelViewMode.Record:
                    PopulatedPlotModel(plotModel);
                    break;

                case ModelViewMode.ICA:
                    PopulatedPlotModel(plotModel);
                    PopulatedPlotWeightsModel(plotWeightsModel);
                    break;
            }

            SubsribePlotEvents(plotModel);

            m_plotView.Model = plotModel;
            m_plotView.ActualController.UnbindAll();
            m_plotView.ActualController.BindMouseDown(OxyMouseButton.Right, PlotCommands.PanAt);

            m_plotWeightsView.Model = plotWeightsModel;
            m_plotWeightsView.ActualController.UnbindAll();
            m_plotWeightsView.ActualController.BindMouseDown(OxyMouseButton.Right, PlotCommands.PanAt);

            m_splitContainer.Panel2Collapsed = (ViewModel.ViewMode != ModelViewMode.ICA);

            switch (ViewModel.ViewMode)
            {
                case ModelViewMode.Record:
                    m_icaButton.Menu = m_icaContextMenuStrip;
                    m_icaButton.BackColor = Color.Transparent;
                    m_icaComposeButton.Visible = false;
                    m_autoButton.Visible = true;
                    break;
                case ModelViewMode.ICA:
                    m_icaButton.Menu = default;
                    m_icaButton.BackColor = SystemColors.ControlDark;
                    m_icaComposeButton.Visible = true;
                    m_autoButton.Visible = false;
                    break;
            }

            m_plotView.Model.ResetAllAxes();

            UpdateSpeedBar();
            UpdateAmplBar();
            UpdateFilterBars();
            UpdateHScrollBar();
        }

        internal void LoadRecord(string path, RecordFactoryOptions options)
        {
            var factory = new RecordFactory();

            ViewModel.SourceRecord = factory.FromFile(path, options);
            ViewModel.RecordOptions = options;
            ViewModel.ProcessedRecord = ViewModel.SourceRecord.Clone();
        }

        internal void ResetRecord()
        {
            ViewModel.ProcessedRecord = ViewModel.SourceRecord.Clone();
        }

        internal void SaveRecord(string path, RecordRange? range = default)
        {
            var factory = new RecordFactory();
            factory.ToFile(path, ViewModel.VisibleRecord.Clone(range));
        }

        static IEnumerable<AnalyzerBase<ComponentArtifactResult>> BuildICAAnalyzers(ICARecord input)
        {
            var res = new List<AnalyzerBase<ComponentArtifactResult>>()
            {
                new ElectrodeArtifactDetector() { Input = input },
                new EyeArtifactDetector() { Input = input },
            };
            return res;
        }

        void PopulatedPlotModel(PlotModel plotModel)
        {
            var record = ViewModel.VisibleRecord;
            var leads = ViewModel.VisibleLeads;

            plotModel.Axes.ToList().ForEach(UnsubsribePlotEvents);
            plotModel.Series.ToList().ForEach(UnsubsribePlotEvents);
            plotModel.Annotations.ToList().ForEach(UnsubsribePlotEvents);

            plotModel.Axes.Clear();
            plotModel.Series.Clear();
            plotModel.Annotations.Clear();

            var xAxis = new TimeSpanAxis()
            {
                Position = AxisPosition.Bottom,
                MajorGridlineStyle = LineStyle.Solid,
                MinorGridlineStyle = LineStyle.Solid,
                FontSize = 9,
                Minimum = 0,
                Maximum = record.Duration / record.SampleRate,
                AbsoluteMinimum = 0,
                AbsoluteMaximum = record.Duration / record.SampleRate,
                MaximumPadding = 0,
                MinimumPadding = 0,
            };
            SubsribePlotEvents(xAxis);
            plotModel.Axes.Add(xAxis);

            var maxSignalAmpl = leads.GetMaximumAbsoluteValue();
            var signalRange = Tuple.Create(-maxSignalAmpl, maxSignalAmpl);

            for (int leadIndex = 0, leadCount = leads.Count(); leadIndex < leadCount; leadIndex++)
            {
                var lead = leads.ElementAt(leadIndex);

                var leadAxisIndex = leadCount - leadIndex - 1;
                var leadAxis = new LinearAxis()
                {
                    Title = lead.Name,
                    Key = lead.Name,
                    StartPosition = (double)(leadAxisIndex) / leadCount,
                    EndPosition = (double)(leadAxisIndex + 1) / leadCount,
                    Position = AxisPosition.Left,
                    MajorGridlineStyle = LineStyle.Solid,
                    Minimum = signalRange.Item1,
                    Maximum = signalRange.Item2,
                    AbsoluteMinimum = signalRange.Item1,
                    AbsoluteMaximum = signalRange.Item2,
                    IsPanEnabled = false,
                    Tag = lead,
                };
                SubsribePlotEvents(leadAxis);

                var leadSeries = new LineSeries()
                {
                    Color = ViewUtilities.GetLeadColor(lead),
                    LineStyle = LineStyle.Solid,
                    YAxisKey = leadAxis.Key,
                    UsePlotModelClipArrea = true,
                    Tag = lead,
                };
                var points = lead.Samples.Select((s, index) => new DataPoint(index / record.SampleRate, s));
                leadSeries.Points.AddRange(points);

                var leadAnnotation = default(Annotation);
                var leadAlternativeSeries = default(LineSeries);

                if (lead is ComponentLead componentLead)
                {
                    if (componentLead.Alternative.Any())
                    {
                        leadAlternativeSeries = new LineSeries()
                        {
                            Color = ViewUtilities.GetAlternativeLeadColor(lead),
                            LineStyle = LineStyle.Solid,
                            YAxisKey = leadAxis.Key,
                            UsePlotModelClipArrea = true,
                            Tag = lead,
                        };
                        var alternativePoints = componentLead.Alternative.Select((s, index) => new DataPoint(index / record.SampleRate, s));
                        leadAlternativeSeries.Points.AddRange(alternativePoints);
                    }

                    if (componentLead.IsArtifact)
                    {
                        leadAnnotation = new ImageAnnotation()
                        {
                            ImageSource = ArtifactImage,
                            Opacity = 0.8,
                            Interpolate = true,
                            X = new PlotLength(0, PlotLengthUnit.RelativeToPlotArea),
                            Y = new PlotLength((double)(leadIndex + 0.5) / leadCount, PlotLengthUnit.RelativeToPlotArea),
                            HorizontalAlignment = OxyPlot.HorizontalAlignment.Left,
                            VerticalAlignment = OxyPlot.VerticalAlignment.Middle,
                            Tag = lead,
                        };

                        SubsribePlotEvents(leadAnnotation);
                    }
                }

                plotModel.Axes.Add(leadAxis);
                plotModel.Series.Add(leadSeries);

                if (leadAlternativeSeries != default)
                {
                    plotModel.Series.Add(leadAlternativeSeries);
                }

                if (leadAnnotation != default)
                {
                    plotModel.Annotations.Add(leadAnnotation);
                }
            }

            if (record.Ranges.Any())
            {
                // hidden annotation axis
                var annotationAxis = new LinearAxis()
                {
                    Key = nameof(record.Ranges),
                    StartPosition = 0,
                    EndPosition = 0 + 0.001,
                    Position = AxisPosition.Left,
                    IsPanEnabled = false,
                    IsAxisVisible = false,
                };
                plotModel.Axes.Add(annotationAxis);

                foreach (var recordRange in record.Ranges)
                {
                    var from = recordRange.From / record.SampleRate;
                    var to = (recordRange.From + recordRange.Duration) / record.SampleRate;

                    var rangeAnnotation = new RectangleAnnotation()
                    {
                        Fill = OxyColor.FromAColor(30, OxyColors.SeaGreen),
                        MinimumX = from,
                        MaximumX = to,
                        ClipByYAxis = false,
                        Tag = recordRange,
                    };

                    var rangePointAnnotation = new PointAnnotation()
                    {
                        ToolTip = string.Join("\n", recordRange.Name,
                                                    $"From: {xAxis.FormatValue(from)}",
                                                    $"To: {xAxis.FormatValue(from)}"),
                        Shape = MarkerType.Diamond,
                        Fill = OxyColors.OrangeRed,
                        Size = 8,
                        X = (from + to) / 2,
                        YAxisKey = nameof(record.Ranges),
                        ClipByYAxis = false,
                        Tag = recordRange,
                    };

                    SubsribePlotEvents(rangePointAnnotation);

                    plotModel.Annotations.Add(rangeAnnotation);
                    plotModel.Annotations.Add(rangePointAnnotation);
                }
            }
        }

        void PopulatedPlotWeightsModel(PlotModel plotModel)
        {
            var record = (ICARecord)ViewModel.VisibleRecord;
            var leads = ViewModel.VisibleLeads;

            plotModel.Axes.Clear();
            plotModel.Series.Clear();
            plotModel.Annotations.Clear();

            var xAxis = new CategoryAxis()
            {
                AxislineStyle = LineStyle.Solid,
                GapWidth = 0.1,
                Position = AxisPosition.Bottom,
                Key = nameof(record.X)
            };

            if (record.X != default)
            {
                xAxis.Labels.AddRange(record.X.Leads.Select(l => l.Name));
            }
            else
            {
                xAxis.Labels.AddRange(Enumerable.Range(1, record.LeadsCount).Select(n => $"X{n}"));
            }

            plotModel.Axes.Add(xAxis);

            for (int componentIndex = 0, componentCount = leads.Count(); componentIndex < componentCount; componentIndex++)
            {
                var component = leads.ElementAt(componentIndex);

                var componentAxisIndex = componentCount - componentIndex - 1;
                var componentAxis = new LinearAxis()
                {
                    Title = component.Name,
                    Key = component.Name,
                    StartPosition = (double)(componentAxisIndex) / componentCount,
                    EndPosition = (double)(componentAxisIndex + 1) / componentCount,
                    Position = AxisPosition.Left,
                    MajorGridlineStyle = LineStyle.Solid,
                    IsPanEnabled = false,
                    Tag = component,
                };
                plotModel.Axes.Add(componentAxis);

                var componentSeries = new BarSeries()
                {
                    XAxisKey = componentAxis.Key,
                    YAxisKey = nameof(record.X),
                    Tag = component,
                };
                componentSeries.Items.AddRange(record.GetMixingVector(componentIndex)
                                                     .Select((w, i) => new BarItem()
                                                     {
                                                         Value = w,
                                                         Color = record.X == default ? ViewUtilities.DefaultLeadColor :
                                                                                       ViewUtilities.GetLeadColor(record.X.Leads[i])
                                                     }));

                plotModel.Series.Add(componentSeries);
            }
        }

        void SubsribePlotEvents(PlotModel plotModel)
        {
            if (plotModel == default)
            {
                return;
            }

#pragma warning disable CS0618 // Type or member is obsolete
            plotModel.MouseDown += OnPlotMouseDown;
            plotModel.MouseUp += OnPlotMouseUp;
            plotModel.MouseMove += OnPlotMouseMove;
#pragma warning restore CS0618 // Type or member is obsolete

            if (plotModel is RecordPlotModel recordPlotModel)
            {
                recordPlotModel.BeforeRendering += OnBeforeRecordPlotModelRendering;
                recordPlotModel.AfterRendering += OnAfterRecordPlotModelRendering;
            }
        }

        void SubsribePlotEvents(PlotElement plotElement)
        {
            if (plotElement == default)
            {
                return;
            }
#pragma warning disable CS0618 // Type or member is obsolete
            if (plotElement is TimeSpanAxis xAxis)
            {
                xAxis.AxisChanged += OnXAxisChanged;
            }
            else
            {
                plotElement.MouseDown += OnPlotMouseDown;
                plotElement.MouseUp += OnPlotMouseUp;
                plotElement.MouseMove += OnPlotMouseMove;
            }
#pragma warning restore CS0618 // Type or member is obsolete
        }

        void UnsubsribePlotEvents(PlotModel plotModel)
        {
            if (plotModel == default)
            {
                return;
            }

#pragma warning disable CS0618 // Type or member is obsolete
            plotModel.MouseDown -= OnPlotMouseDown;
            plotModel.MouseUp -= OnPlotMouseUp;
            plotModel.MouseMove -= OnPlotMouseMove;
#pragma warning restore CS0618 // Type or member is obsolete

            if (plotModel is RecordPlotModel recordPlotModel)
            {
                recordPlotModel.BeforeRendering -= OnBeforeRecordPlotModelRendering;
                recordPlotModel.AfterRendering -= OnAfterRecordPlotModelRendering;
            }
        }

        void UnsubsribePlotEvents(PlotElement plotElement)
        {
            if (plotElement == default)
            {
                return;
            }

#pragma warning disable CS0618 // Type or member is obsolete
            if (plotElement is TimeSpanAxis xAxis)
            {
                xAxis.AxisChanged -= OnXAxisChanged;
            }
            else
            {
                plotElement.MouseDown -= OnPlotMouseDown;
                plotElement.MouseUp -= OnPlotMouseUp;
                plotElement.MouseMove -= OnPlotMouseMove;
            }
#pragma warning restore CS0618 // Type or member is obsolete
        }

        void UpdateHScrollBar()
        {
            var xAxis = PlotModelXAxis;
            if (xAxis != default)
            {
                var wholeRange = ViewModel.VisibleRecord.Duration;
                var viewportRange = (int)((xAxis.ActualMaximum - xAxis.ActualMinimum) * ViewModel.VisibleRecord.SampleRate);
                var position = (int)(xAxis.ActualMinimum * ViewModel.VisibleRecord.SampleRate);
                var visible = wholeRange != viewportRange;

                m_plotViewHScrollBar.Minimum = 0;
                m_plotViewHScrollBar.SmallChange = viewportRange / 50;
                m_plotViewHScrollBar.LargeChange = viewportRange / 2;
                m_plotViewHScrollBar.Maximum = (wholeRange - viewportRange) + m_plotViewHScrollBar.LargeChange;
                m_plotViewHScrollBar.Value = position;
                m_plotViewHScrollBar.Visible = visible;
            }
        }

        void UpdateSpeedBar()
        {
            var item = m_speedComboBox.Items
                                      .Cast<SpeedItem>()
                                      .FirstOrDefault(s => s.Value == ViewModel.Speed.Value);
            m_speedComboBox.SelectedItem = item;
        }

        void UpdateAmplBar()
        {
            var item = m_amplComboBox.Items
                                     .Cast<AmplItem>()
                                     .FirstOrDefault(a => a.Value == ViewModel.Amplitude.Value);
            m_amplComboBox.SelectedItem = item;
        }

        void UpdateFilterBars()
        {
            // low cut off
            {
                var item = m_filterLowCutOffComboBox.Items
                                         .Cast<FrequencyItem>()
                                         .FirstOrDefault(a => a.Value == ViewModel.CutOffLowFrequency.Value);
                m_filterLowCutOffComboBox.SelectedItem = item;
            }

            // high cut off
            {
                var item = m_filterHighCutOffComboBox.Items
                                         .Cast<FrequencyItem>()
                                         .FirstOrDefault(a => a.Value == ViewModel.CutOffHighFrequency.Value);
                m_filterHighCutOffComboBox.SelectedItem = item;
            }
        }

        void ScrollPlot(int samplePosition)
        {
            Debug.Assert(samplePosition >= 0);

            ScrollPlot(new TimePositionItem() { Value = samplePosition / ViewModel.VisibleRecord.SampleRate });
        }

        void ScrollPlot(TimePositionItem position)
        {
            Debug.Assert(position.Value >= 0);

            var xAxis = PlotModelXAxis;
            if ((xAxis != default) &&
                !InPlotRescaleExecution)
            {
                InPlotRescaleExecution = true;

                var viewportRange = xAxis.ActualMaximum - xAxis.ActualMinimum;
                var newPosition = position.Value;

                ViewModel.Position = position;

                xAxis.Minimum = newPosition;
                xAxis.Maximum = newPosition + viewportRange;
                xAxis.Reset();
                PlotModel.InvalidatePlot(false);

                InPlotRescaleExecution = false;
            }
        }

        void SpeedPlot(SpeedItem speed)
        {
            var xAxis = PlotModelXAxis;

            if ((xAxis != default) &&
                !InPlotRescaleExecution)
            {
                InPlotRescaleExecution = true;

                ViewModel.Speed = speed;

                if (speed.Value > 0)
                {
                    var ptPerMm = ViewUtilities.GetDPMM(this).X;
                    var viewportRange_mm = PlotModel.PlotArea.Width / ptPerMm;
                    var viewportRange_sec = viewportRange_mm / speed.Value;

                    xAxis.Minimum = ViewModel.Position.Value;
                    xAxis.Maximum = ViewModel.Position.Value + viewportRange_sec;
                }
                else
                {
                    ViewModel.Position = TimePositionItem.Default;

                    xAxis.Minimum = xAxis.AbsoluteMinimum;
                    xAxis.Maximum = xAxis.AbsoluteMaximum;
                }

                xAxis.Reset();
                PlotModel.InvalidatePlot(false);

                UpdateHScrollBar();

                InPlotRescaleExecution = false;
            }
        }

        void AmplifirePlot(AmplItem amplitude)
        {
            var xAxis = PlotModelXAxis;

            if ((xAxis != default) &&
                !InPlotRescaleExecution)
            {
                InPlotRescaleExecution = true;

                ViewModel.Amplitude = amplitude;

                var maxSignalAmpl = new Lazy<double>(ViewModel.VisibleLeads.GetMaximumAbsoluteValue);

                foreach (var yAxis in PlotModelYAxes)
                {
                    if (amplitude.Value > 0)
                    {
                        var ptPerMm = ViewUtilities.GetDPMM(this).Y;

                        var viewportRange_pt = Math.Abs(yAxis.Transform(yAxis.ActualMaximum) - yAxis.Transform(0));
                        var viewportRange_mm = viewportRange_pt / ptPerMm;
                        var viewportRange_mkV = viewportRange_mm * (amplitude.Value / 10);

                        yAxis.Minimum = -viewportRange_mkV;
                        yAxis.Maximum = viewportRange_mkV;
                        yAxis.AbsoluteMinimum = -viewportRange_mkV;
                        yAxis.AbsoluteMaximum = viewportRange_mkV;
                    }
                    else
                    {
                        yAxis.Minimum = -maxSignalAmpl.Value;
                        yAxis.Maximum = maxSignalAmpl.Value;
                        yAxis.AbsoluteMinimum = -maxSignalAmpl.Value;
                        yAxis.AbsoluteMaximum = maxSignalAmpl.Value;
                    }

                    yAxis.Reset();
                }

                PlotModel.InvalidatePlot(false);
                UpdateHScrollBar();

                InPlotRescaleExecution = false;
            }
        }

        void FilterLowCutOff(FrequencyItem cutOff)
        {
            if (cutOff.Value != ViewModel.CutOffLowFrequency.Value)
            {
                if (!cutOff.HasValue ||
                    !ViewModel.CutOffHighFrequency.HasValue ||
                    (cutOff.Value < ViewModel.CutOffHighFrequency.Value))
                {
                    ViewModel.CutOffLowFrequency = cutOff;
                    UpdatePlot();
                }
                else
                {
                    UpdateFilterBars();
                }
            }
        }

        void FilterHighCutOff(FrequencyItem cutOff)
        {
            if (cutOff.Value != ViewModel.CutOffHighFrequency.Value)
            {
                if (!cutOff.HasValue ||
                    !ViewModel.CutOffLowFrequency.HasValue ||
                    (cutOff.Value > ViewModel.CutOffLowFrequency.Value))
                {
                    ViewModel.CutOffHighFrequency = cutOff;
                    UpdatePlot();
                }
                else
                {
                    UpdateFilterBars();
                }
            }
        }

        #region Input Events

        void OnPlotMouseDown(object? sender, OxyMouseDownEventArgs e)
        {
            void wasHandled(string _, bool handled) { e.Handled = handled; }

            LastPoint = new Point((int)e.Position.X, (int)e.Position.Y);

            var mouseEvent = StateMachine.EventMouseDown;
            mouseEvent.AfterEvent += wasHandled;
            mouseEvent.Init(this, sender, e);
            mouseEvent.Fire();
            mouseEvent.AfterEvent -= wasHandled;
        }

        void OnPlotMouseUp(object? sender, OxyMouseEventArgs e)
        {
            void wasHandled(string _, bool handled) { e.Handled = handled; }

            LastPoint = new Point((int)e.Position.X, (int)e.Position.Y);

            var mouseEvent = StateMachine.EventMouseUp;
            mouseEvent.AfterEvent += wasHandled;
            mouseEvent.Init(this, sender, e);
            mouseEvent.Fire();
            mouseEvent.AfterEvent -= wasHandled;
        }

        void OnPlotMouseMove(object? sender, OxyMouseEventArgs e)
        {
            void wasHandled(string _, bool handled) { e.Handled = handled; }

            LastPoint = new Point((int)e.Position.X, (int)e.Position.Y);

            var mouseEvent = StateMachine.EventMouseMove;
            mouseEvent.AfterEvent += wasHandled;
            mouseEvent.Init(this, sender, e);
            mouseEvent.Fire();
            mouseEvent.AfterEvent -= wasHandled;
        }

        #endregion

        #region Plot Model Events

        void OnBeforeRecordPlotModelRendering(object? sender, EventArgs e)
        {
            if (NeedPlotRescale)
            {
                PlotModel.LockRenderingContext();
            }
        }

        void OnAfterRecordPlotModelRendering(object? sender, EventArgs e)
        {
            if (NeedPlotRescale)
            {
                NeedPlotRescale = false;

                PlotModel.UnlockRenderingContext();

                ScrollPlot(ViewModel.Position);
                SpeedPlot(ViewModel.Speed);
                AmplifirePlot(ViewModel.Amplitude);
            }
        }

        void OnXAxisChanged(object? sender, AxisChangedEventArgs e)
        {
            if (!NeedPlotRescale && sender is TimeSpanAxis xAxis)
            {
                ViewModel.Position = new TimePositionItem() { Value = xAxis.ActualMinimum };

                UpdateHScrollBar();
            }
        }

        void OnPlotViewResized(object sender, EventArgs e)
        {
            NeedPlotRescale = true;
        }

        #endregion

        #region Controls Events

        void OnLoad(object sender, EventArgs e)
        {
            LoadRecord(@".\EEGData\Test1\EEG Eye State.arff", RecordFactoryOptions.DefaultEEGNoFilter);

            UpdatePlot(ModelViewMode.Record);
            StateMachine.SwitchState(EEGRecordState.Name);
        }

        void OnSpeedSelected(object sender, EventArgs e)
        {
            if (m_speedComboBox.SelectedItem is SpeedItem selectedItem)
            {
                SpeedPlot(selectedItem);
            }
        }

        void OnAmplSelected(object sender, EventArgs e)
        {
            if (m_amplComboBox.SelectedItem is AmplItem selectedItem)
            {
                AmplifirePlot(selectedItem);
            }
        }

        void OnFilterLowCutOffSelected(object sender, EventArgs e)
        {
            if (m_filterLowCutOffComboBox.SelectedItem is FrequencyItem selectedItem)
            {
                FilterLowCutOff(selectedItem);
            }
        }

        void OnFilterHighCutOffSelected(object sender, EventArgs e)
        {
            if (m_filterHighCutOffComboBox.SelectedItem is FrequencyItem selectedItem)
            {
                FilterHighCutOff(selectedItem);
            }
        }

        void OnHScroll(object sender, ScrollEventArgs e)
        {
            ScrollPlot(m_plotViewHScrollBar.Value);
        }

        void OnAutoRanges(object sender, EventArgs e)
        {
            var rangeDetector = new ArtifactCandidateRangeDetector() { Input = ViewModel.VisibleRecord };
            var result = rangeDetector.Analyze();
            if (result.Succeed)
            {
                UpdatePlot();
            }
        }

        void OnAutoClean(object sender, EventArgs e)
        {
            using (var progressForm = new ProgressForm())
            {
                progressForm.Start += async form =>
                {
                    var taskResult = await Task.Run(() =>
                    {
                        var autoCleaner = new AutoArtifactCleaner() { Input = ViewModel.VisibleRecord };
                        var result = autoCleaner.Analyze();
                        return result;
                    });

                    if (taskResult.Succeed)
                    {
                        ViewModel.ProcessedRecord = taskResult.Output!;
                        UpdatePlot();
                    }

                    form.DialogResult = DialogResult.OK;
                };

                progressForm.ShowDialog();
            }
        }

        void OnResetDataToOrigin(object sender, EventArgs e)
        {
            ResetRecord();

            UpdatePlot(ModelViewMode.Record);
            StateMachine.SwitchState(EEGRecordState.Name);
        }

        void OnLoadTestData(object sender, EventArgs e)
        {
            m_openFileDialog.InitialDirectory = Directory.GetCurrentDirectory();

            if (m_openFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                LoadRecord(m_openFileDialog.FileName, RecordFactoryOptions.DefaultEmpty);

                UpdatePlot(ModelViewMode.Record);
                StateMachine.SwitchState(EEGRecordState.Name);
            }
        }

        void OnLoadEEGData(object sender, EventArgs e)
        {
            m_openFileDialog.InitialDirectory = Directory.GetCurrentDirectory();

            if (m_openFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                LoadRecord(m_openFileDialog.FileName, RecordFactoryOptions.DefaultEEG);

                UpdatePlot(ModelViewMode.Record);
                StateMachine.SwitchState(EEGRecordState.Name);
            }
        }

        void OnSaveData(object sender, EventArgs e)
        {
            m_saveFileDialog.InitialDirectory = Directory.GetCurrentDirectory();

            if (m_saveFileDialog.ShowDialog(this) == DialogResult.OK)
            {
                SaveRecord(m_saveFileDialog.FileName);
            }
        }

        #endregion
    }
}
