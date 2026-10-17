using mobileappbackend1.ML;

namespace mobileappbackend1.Services
{
    // Thin seam over PredictionEngineService so plan-scoring tests can swap in
    // a fake without wiring up MLContext, a saved model file, or MongoDB. The
    // production implementation is still PredictionEngineService; this
    // interface deliberately exposes only the two members plan scoring needs.
    public interface IProgressPredictor
    {
        bool IsReady { get; }
        ProgressPrediction? Predict(ProgressInput input);
    }
}
