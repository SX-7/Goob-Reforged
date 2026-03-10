// SPDX-FileCopyrightText: 2026 Goob Station Contributors
//
// SPDX-License-Identifier: MPL-2.0

using Content.Goobstation.Shared.Supermatter.Components;
using Content.Shared.Atmos;
using Content.Shared.Radiation.Components;

namespace Content.Goobstation.Server.Supermatter.Systems;

public sealed partial class SupermatterSystem
{
    /// <summary>Handle power and radiation output depending on atmospheric things.</summary>
    /// <param name="ent">Entity to process atmos for.</param>
    private void ProcessAtmos(Entity<SupermatterComponent> ent)
    {
        var sm = ent.Comp;

        if (!_atmosphere.TryGetContainingMixture(out var mix, ent))
            return;

        using var absorbed = new GasWrapper(mix, sm.GasEfficiency, _atmosphere);

        var (radModifier, _, moleModifier, heatModifier, _) = absorbed.Gas.GetGasModifiers();
        var co2Modifier = GetCo2Modifier(sm, absorbed);

        AddPowerToCrystal(sm, absorbed, heatModifier);
        GenerateOutputs(ent, absorbed, radModifier, moleModifier, heatModifier);
        ScaleDownPower(sm, co2Modifier);
    }

    private static void AddPowerToCrystal(SupermatterComponent sm, in GasWrapper absorbed, float heatModifier)
    {
        ConsumeMatterPower(sm);
        ConsumeAmmonia(sm, absorbed);
        sm.Power = Math.Max(GetTemperaturePowerGain(sm, absorbed.Gas.Temperature, heatModifier) + sm.Power, 0);
    }

    /// <summary>Calculate crystal power gained from ambient gas temperature.</summary>
    private static float GetTemperaturePowerGain(SupermatterComponent sm, float temperature, float heatModifier)
    {
        // heatModifier has a neutral baseline of 1 (empty mix). Subtracting it gives the net gas contribution.
        var powerRatio = Math.Clamp(heatModifier - 1f, 0f, 1f);
        var tempFactor = powerRatio > sm.TempFactorHighThreshold ? sm.TempFactorHigh : sm.TempFactorBase;
        return temperature * tempFactor / Atmospherics.T0C * powerRatio;
    }

    private void GenerateOutputs(Entity<SupermatterComponent> ent, in GasWrapper absorbed, float radModifier, float moleModifier, float heatModifier)
    {
        var sm = ent.Comp;

        if (TryComp<RadiationSourceComponent>(ent, out var rad))
            rad.Intensity = sm.Power * radModifier * sm.RadiationOutputFactor;

        var energy = sm.Power * sm.ReactionPowerModifier;

        // Release waste gases, scaled by mole modifier.
        absorbed.Gas.AdjustMoles(Gas.Oxygen, Math.Max(moleModifier * (energy + absorbed.Gas.Temperature - Atmospherics.T0C) * sm.OxygenReleaseEfficiencyModifier, 0f));
        absorbed.Gas.AdjustMoles(Gas.Plasma, Math.Max(moleModifier * sm.PlasmaReleaseModifier * energy, 0f));

        // Increase temperature, scaled by heatModifier.
        absorbed.Gas.Temperature += energy * heatModifier * sm.ThermalReleaseModifier;
    }

    private static void ScaleDownPower(SupermatterComponent sm, float co2Modifier)
    {
        // I'd recommend plotting these two if you want to get it but in general this lets it need less input to stay under power threshold/scaler than above
        // Hardcoded to discourage YAML majors
        const float powerReductionScaler = 500f;
        var powerReduction = float.Pow(sm.Power / powerReductionScaler, 3f);

        // Cap at MaxPowerLossFraction to prevent instant drain on large power spikes.
        sm.Power = Math.Max(sm.Power - Math.Min(powerReduction, sm.Power * sm.MaxPowerLossFraction) * co2Modifier, 0f);
    }

    private static void ConsumeMatterPower(SupermatterComponent sm)
    {
        if (sm.MatterPower <= 0)
            return;

        var removedMatter = Math.Min(sm.MatterPower, sm.MatterPowerConsumedPerCycle);
        sm.Power += removedMatter * sm.MatterPowerConversion;
        sm.MatterPower -= removedMatter;
    }

    private static void ConsumeAmmonia(SupermatterComponent sm, in GasWrapper gas)
    {
        // Yeah, it consumes all ammonia in one tick cuz it's funny af
        var ammoniaGasMoles = gas.Gas.GetMoles(Gas.Ammonia);
        gas.Gas.SetMoles(Gas.Ammonia, 0f);
        sm.Power += ammoniaGasMoles * sm.AmmoniaEnergyPerMole;
    }

    private static float GetCo2Modifier(SupermatterComponent sm, in GasWrapper absorbed)
    {
        var co2Ratio = absorbed.Gas.GetGasMolarPercentage(Gas.CarbonDioxide);
        var underThresholdScaler = Math.Min(
            Math.Clamp(co2Ratio / sm.PowerlossInhibitionGasThreshold, 0, 1),
            Math.Clamp(absorbed.Gas.TotalMoles / sm.PowerlossInhibitionMoleThreshold, 0, 1)
            );
        var moleBoost = Math.Clamp(absorbed.Gas.TotalMoles / sm.PowerlossInhibitionMoleBoostThreshold, 1f, 1.5f);

        // Apply CO2 ratio if thresholds are met, otherwise limit the ratio according to how far away we are from thresholds
        var powerlossDynamicScaling = co2Ratio * underThresholdScaler;
        return Math.Clamp(1f - powerlossDynamicScaling * moleBoost, 0f, 1f);
    }
}
