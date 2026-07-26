using NUnit.Framework;
using ValveResourceFormat.Renderer.SceneEnvironment;

namespace Tests.Renderer
{
    public class TonemapSettingsTest
    {
        [TestCase(0.05f)]
        [TestCase(0.18f)]
        [TestCase(0.9f)]
        public void InvertTonemappingRoundTrips(float displayValue)
        {
            var settings = new TonemapSettings();
            var input = settings.InvertTonemapping(displayValue);

            var tonemapped = settings.ApplyTonemapping(input) / settings.ApplyTonemapping(settings.WhitePoint);
            Assert.That(tonemapped, Is.EqualTo(displayValue).Within(1e-4f));
        }
    }
}
