﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Threading;
using JetBrains.Annotations;
using Newtonsoft.Json;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ListExtensions;
using osu.Framework.Lists;
using osu.Game.Audio;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.ControlPoints;
using osu.Game.Beatmaps.Legacy;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Scoring;

namespace osu.Game.Rulesets.Objects
{
    /// <summary>
    /// A HitObject describes an object in a Beatmap.
    /// <para>
    /// HitObjects may contain more properties for which you should be checking through the IHas* types.
    /// </para>
    /// </summary>
    public class HitObject
    {
        /// <summary>
        /// A small adjustment to the start time of control points to account for rounding/precision errors.
        /// </summary>
        private const double control_point_leniency = 1;

        /// <summary>
        /// Invoked after <see cref="ApplyDefaults"/> has completed on this <see cref="HitObject"/>.
        /// </summary>
        public event Action<HitObject> DefaultsApplied;

        public readonly Bindable<double> StartTimeBindable = new BindableDouble();

        /// <summary>
        /// The time at which the HitObject starts.
        /// </summary>
        public virtual double StartTime
        {
            get => StartTimeBindable.Value;
            set => StartTimeBindable.Value = value;
        }

        public readonly BindableList<HitSampleInfo> SamplesBindable = new BindableList<HitSampleInfo>();

        /// <summary>
        /// The samples to be played when this hit object is hit.
        /// <para>
        /// In the case of <see cref="IHasRepeats"/> types, this is the sample of the curve body
        /// and can be treated as the default samples for the hit object.
        /// </para>
        /// </summary>
        public IList<HitSampleInfo> Samples
        {
            get => SamplesBindable;
            set
            {
                SamplesBindable.Clear();
                SamplesBindable.AddRange(value);
            }
        }

        public SampleControlPoint SampleControlPoint = SampleControlPoint.DEFAULT;
        public DifficultyControlPoint DifficultyControlPoint = DifficultyControlPoint.DEFAULT;

        /// <summary>
        /// Whether this <see cref="HitObject"/> is in Kiai time.
        /// </summary>
        [JsonIgnore]
        public bool Kiai { get; private set; }

        /// <summary>
        /// The hit windows for this <see cref="HitObject"/>.
        /// </summary>
        [JsonIgnore]
        public HitWindows HitWindows { get; set; }

        private readonly List<HitObject> nestedHitObjects = new List<HitObject>();

        [JsonIgnore]
        public SlimReadOnlyListWrapper<HitObject> NestedHitObjects => nestedHitObjects.AsSlimReadOnly();

        public HitObject()
        {
            StartTimeBindable.ValueChanged += time =>
            {
                double offset = time.NewValue - time.OldValue;

                foreach (var nested in nestedHitObjects)
                    nested.StartTime += offset;

                if (DifficultyControlPoint != DifficultyControlPoint.DEFAULT)
                    DifficultyControlPoint.Time = time.NewValue;

                if (SampleControlPoint != SampleControlPoint.DEFAULT)
                    SampleControlPoint.Time = this.GetEndTime() + control_point_leniency;
            };
        }

        /// <summary>
        /// Applies default values to this HitObject.
        /// </summary>
        /// <param name="controlPointInfo">The control points.</param>
        /// <param name="difficulty">The difficulty settings to use.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        public void ApplyDefaults(ControlPointInfo controlPointInfo, IBeatmapDifficultyInfo difficulty, CancellationToken cancellationToken = default)
        {
            var legacyInfo = controlPointInfo as LegacyControlPointInfo;

            if (legacyInfo != null)
            {
                DifficultyControlPoint = (DifficultyControlPoint)legacyInfo.DifficultyPointAt(StartTime).DeepClone();
                DifficultyControlPoint.Time = StartTime;
            }
            else if (DifficultyControlPoint == DifficultyControlPoint.DEFAULT)
                DifficultyControlPoint = new DifficultyControlPoint();

            ApplyDefaultsToSelf(controlPointInfo, difficulty);

            // This is done here after ApplyDefaultsToSelf as we may require custom defaults to be applied to have an accurate end time.
            if (legacyInfo != null)
            {
                SampleControlPoint = (SampleControlPoint)legacyInfo.SamplePointAt(this.GetEndTime() + control_point_leniency).DeepClone();
                SampleControlPoint.Time = this.GetEndTime() + control_point_leniency;
            }
            else if (SampleControlPoint == SampleControlPoint.DEFAULT)
                SampleControlPoint = new SampleControlPoint();

            nestedHitObjects.Clear();

            CreateNestedHitObjects(cancellationToken);

            if (this is IHasComboInformation hasCombo)
            {
                foreach (HitObject hitObject in nestedHitObjects)
                {
                    if (hitObject is IHasComboInformation n)
                    {
                        n.ComboIndexBindable.BindTo(hasCombo.ComboIndexBindable);
                        n.ComboIndexWithOffsetsBindable.BindTo(hasCombo.ComboIndexWithOffsetsBindable);
                        n.IndexInCurrentComboBindable.BindTo(hasCombo.IndexInCurrentComboBindable);
                    }
                }
            }

            nestedHitObjects.Sort((h1, h2) => h1.StartTime.CompareTo(h2.StartTime));

            foreach (var h in nestedHitObjects)
                h.ApplyDefaults(controlPointInfo, difficulty, cancellationToken);

            DefaultsApplied?.Invoke(this);
        }

        protected virtual void ApplyDefaultsToSelf(ControlPointInfo controlPointInfo, IBeatmapDifficultyInfo difficulty)
        {
            Kiai = controlPointInfo.EffectPointAt(StartTime + control_point_leniency).KiaiMode;

            HitWindows ??= CreateHitWindows();
            HitWindows?.SetDifficulty(difficulty.OverallDifficulty);
        }

        protected virtual void CreateNestedHitObjects(CancellationToken cancellationToken)
        {
        }

        protected void AddNested(HitObject hitObject) => nestedHitObjects.Add(hitObject);

        /// <summary>
        /// Creates the <see cref="Judgement"/> that represents the scoring information for this <see cref="HitObject"/>.
        /// </summary>
        [NotNull]
        public virtual Judgement CreateJudgement() => new Judgement();

        /// <summary>
        /// Creates the <see cref="HitWindows"/> for this <see cref="HitObject"/>.
        /// This can be null to indicate that the <see cref="HitObject"/> has no <see cref="HitWindows"/> and timing errors should not be displayed to the user.
        /// <para>
        /// This will only be invoked if <see cref="HitWindows"/> hasn't been set externally (e.g. from a <see cref="BeatmapConverter{T}"/>.
        /// </para>
        /// </summary>
        [NotNull]
        protected virtual HitWindows CreateHitWindows() => new HitWindows();
    }

    public static class HitObjectExtensions
    {
        /// <summary>
        /// Returns the end time of this object.
        /// </summary>
        /// <remarks>
        /// This returns the <see cref="IHasDuration.EndTime"/> where available, falling back to <see cref="HitObject.StartTime"/> otherwise.
        /// </remarks>
        /// <param name="hitObject">The object.</param>
        /// <returns>The end time of this object.</returns>
        public static double GetEndTime(this HitObject hitObject) => (hitObject as IHasDuration)?.EndTime ?? hitObject.StartTime;
    }
}
