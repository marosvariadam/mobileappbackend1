using Microsoft.ML;
using Microsoft.ML.Transforms;

namespace mobileappbackend1.ML
{
    /// <summary>Builds the ML.NET regression pipeline, fits on labeled rows, and writes the model to disk.</summary>
    public class ProgressTrainer
    {
        public const int MinLabeledRows = 50;

        private readonly MLContext _ml;

        public ProgressTrainer(MLContext ml)
        {
            _ml = ml;
        }

        public TrainResult TrainAndSave(IReadOnlyList<LabeledTrainingRow> rows, string modelPath)
        {
            if (rows.Count < MinLabeledRows)
                throw new InvalidOperationException(
                    $"Need at least {MinLabeledRows} labeled rows to train; got {rows.Count}.");

            var inputs = rows.Select(ProgressInput.From).ToList();
            var data = _ml.Data.LoadFromEnumerable(inputs);
            var split = _ml.Data.TrainTestSplit(data, testFraction: 0.2, seed: 1);

            var pipeline = _ml.Transforms.Categorical.OneHotEncoding(new[]
                {
                    new InputOutputColumnPair("ExerciseEncoded", nameof(ProgressInput.ExerciseName)),
                    new InputOutputColumnPair("MuscleEncoded",   nameof(ProgressInput.MuscleGroup)),
                    new InputOutputColumnPair("FocusEncoded",    nameof(ProgressInput.Focus)),
                }, OneHotEncodingEstimator.OutputKind.Indicator)
                .Append(_ml.Transforms.Concatenate(
                    "Features",
                    "ExerciseEncoded", "MuscleEncoded", "FocusEncoded",
                    nameof(ProgressInput.VolumeKg),
                    nameof(ProgressInput.Est1Rm),
                    nameof(ProgressInput.AvgRpe),
                    nameof(ProgressInput.SetCount),
                    nameof(ProgressInput.TotalReps),
                    nameof(ProgressInput.OverlapScore),
                    nameof(ProgressInput.BodyweightKg),
                    nameof(ProgressInput.TrainingAgeWeeks),
                    nameof(ProgressInput.IsBeginner),
                    nameof(ProgressInput.PrevWeekEst1Rm),
                    nameof(ProgressInput.PrevWeekVolumeKg)))
                .Append(_ml.Transforms.NormalizeMinMax("Features"))
                .Append(_ml.Regression.Trainers.LightGbm(
                    labelColumnName: nameof(ProgressInput.Label),
                    featureColumnName: "Features"));

            var model = pipeline.Fit(split.TrainSet);

            var scored = model.Transform(split.TestSet);
            var metrics = _ml.Regression.Evaluate(scored, labelColumnName: nameof(ProgressInput.Label));

            var dir = Path.GetDirectoryName(modelPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            _ml.Model.Save(model, data.Schema, modelPath);

            return new TrainResult(
                RowCount:  inputs.Count,
                Rmse:      metrics.RootMeanSquaredError,
                MeanAbsErr: metrics.MeanAbsoluteError,
                RSquared:  metrics.RSquared);
        }
    }

    public record TrainResult(int RowCount, double Rmse, double MeanAbsErr, double RSquared);
}
