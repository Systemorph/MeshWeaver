using System.Text.Json.Serialization;
using MeshWeaver.Charting.Enums;

namespace MeshWeaver.Charting.Models.Options.Animation
{
    // https://www.chartjs.org/docs/3.5.1/configuration/animations.html#animation
    public record Animation
    {
        /// <summary>
        /// The number of milliseconds an animation takes.
        /// </summary>
        public int? Duration { get; init; }

        /// <summary>
        /// Easing function to use. Available options are: 'linear', 'easeInQuad', 'easeOutQuad', 'easeInOutQuad', 'easeInCubic', 'easeOutCubic', 'easeInOutCubic', 'easeInQuart', 'easeOutQuart', 'easeInOutQuart', 'easeInQuint', 'easeOutQuint', 'easeInOutQuint', 'easeInSine', 'easeOutSine', 'easeInOutSine', 'easeInExpo', 'easeOutExpo', 'easeInOutExpo', 'easeInCirc', 'easeOutCirc', 'easeInOutCirc', 'easeInElastic', 'easeOutElastic', 'easeInOutElastic', 'easeInBack', 'easeOutBack', 'easeInOutBack', 'easeInBounce', 'easeOutBounce', 'easeInOutBounce'.
        /// </summary>
        public Easing? Easing { get; init; }

        /// <summary>
        /// Delay before starting the animations.
        /// </summary>
        public int? Delay { get; init; }

        /// <summary>
        /// If set to true, the animations loop endlessly.
        /// </summary>
        public bool? Loop { get; init; }

        /// <summary>
        /// Callback called on each step of an animation. Passed a single argument, an object, containing the chart instance and an object with details of the animation.
        /// </summary>
        public object OnProgress { get; init; }

        /// <summary>
        /// Callback called at the end of an animation. Passed the same arguments as onProgress.
        /// </summary>
        public object OnComplete { get; init; }
    }
}
