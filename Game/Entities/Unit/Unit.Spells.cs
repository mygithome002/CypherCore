﻿/*
 * Copyright (C) 2012-2017 CypherCore <http://github.com/CypherCore>
 * 
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with this program.  If not, see <http://www.gnu.org/licenses/>.
 */

using Framework.Constants;
using Framework.Dynamic;
using Game.BattleGrounds;
using Game.Network.Packets;
using Game.Spells;
using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;

namespace Game.Entities
{
    public partial class Unit
    {
        public virtual bool HasSpell(uint spellId) { return false; }

        // function uses real base points (typically value - 1)
        public int CalculateSpellDamage(Unit target, SpellInfo spellProto, uint effect_index, int? basePoints = null, int itemLevel = -1)
        {
            SpellEffectInfo effect = spellProto.GetEffect(GetMap().GetDifficultyID(), effect_index);
            return effect != null ? effect.CalcValue(this, basePoints, target) : 0;
        }
        public int CalculateSpellDamage(Unit target, SpellInfo spellProto, uint effect_index, out float variance, int? basePoints = null, int itemLevel = -1)
        {
            SpellEffectInfo effect = spellProto.GetEffect(GetMap().GetDifficultyID(), effect_index);
            variance = 0.0f;
            return effect != null ? effect.CalcValue(out variance, this, basePoints, target, itemLevel) : 0;
        }

        public int SpellBaseDamageBonusDone(SpellSchoolMask schoolMask)
        {
            if (IsTypeId(TypeId.Player))
            {
                float overrideSP = GetFloatValue(PlayerFields.OverrideSpellPowerByApPct);
                if (overrideSP > 0.0f)
                    return (int)(MathFunctions.CalculatePct(GetTotalAttackPowerValue(WeaponAttackType.BaseAttack), overrideSP) + 0.5f);
            }

            int DoneAdvertisedBenefit = 0;

            var mDamageDone = GetAuraEffectsByType(AuraType.ModDamageDone);
            foreach (var eff in mDamageDone)
            {
                if ((eff.GetMiscValue() & (int)schoolMask) != 0)
                    DoneAdvertisedBenefit += eff.GetAmount();
            }

            if (IsTypeId(TypeId.Player))
            {
                // Base value
                DoneAdvertisedBenefit += (int)ToPlayer().GetBaseSpellPowerBonus();

                // Check if we are ever using mana - PaperDollFrame.lua
                if (GetPowerIndex(PowerType.Mana) != (uint)PowerType.Max)
                    DoneAdvertisedBenefit += Math.Max(0, (int)GetStat(Stats.Intellect));  // spellpower from intellect

                // Damage bonus from stats
                var mDamageDoneOfStatPercent = GetAuraEffectsByType(AuraType.ModSpellDamageOfStatPercent);
                foreach (var eff in mDamageDoneOfStatPercent)
                {
                    if (Convert.ToBoolean(eff.GetMiscValue() & (int)schoolMask))
                    {
                        // stat used stored in miscValueB for this aura
                        Stats usedStat = (Stats)eff.GetMiscValueB();
                        DoneAdvertisedBenefit += (int)MathFunctions.CalculatePct(GetStat(usedStat), eff.GetAmount());
                    }
                }
                // ... and attack power
                var mDamageDonebyAP = GetAuraEffectsByType(AuraType.ModSpellDamageOfAttackPower);
                foreach (var eff in mDamageDonebyAP)
                    if (Convert.ToBoolean(eff.GetMiscValue() & (int)schoolMask))
                        DoneAdvertisedBenefit += (int)MathFunctions.CalculatePct(GetTotalAttackPowerValue(WeaponAttackType.BaseAttack), eff.GetAmount());

            }
            return DoneAdvertisedBenefit;
        }

        public int SpellBaseDamageBonusTaken(SpellSchoolMask schoolMask)
        {
            int TakenAdvertisedBenefit = 0;

            var mDamageTaken = GetAuraEffectsByType(AuraType.ModDamageTaken);
            foreach (var eff in mDamageTaken)
                if ((eff.GetMiscValue() & (int)schoolMask) != 0)
                    TakenAdvertisedBenefit += eff.GetAmount();

            return TakenAdvertisedBenefit;
        }

        public uint SpellDamageBonusDone(Unit victim, SpellInfo spellProto, uint pdamage, DamageEffectType damagetype, SpellEffectInfo effect, uint stack = 1)
        {
            if (spellProto == null || victim == null || damagetype == DamageEffectType.Direct)
                return pdamage;

            // Some spells don't benefit from done mods
            if (spellProto.HasAttribute(SpellAttr3.NoDoneBonus))
                return pdamage;

            // For totems get damage bonus from owner
            if (IsTypeId(TypeId.Unit) && IsTotem())
            {
                Unit owner1 = GetOwner();
                if (owner1 != null)
                    return owner1.SpellDamageBonusDone(victim, spellProto, pdamage, damagetype, effect, stack);
            }

            int DoneTotal = 0;

            // Done fixed damage bonus auras
            int DoneAdvertisedBenefit = SpellBaseDamageBonusDone(spellProto.GetSchoolMask());
            // Pets just add their bonus damage to their spell damage
            // note that their spell damage is just gain of their own auras
            if (HasUnitTypeMask(UnitTypeMask.Guardian))
                DoneAdvertisedBenefit += ((Guardian)this).GetBonusDamage();

            // Check for table values
            if (effect.BonusCoefficientFromAP > 0.0f)
            {
                float ApCoeffMod = effect.BonusCoefficientFromAP;
                Player modOwner = GetSpellModOwner();
                if (modOwner)
                {
                    ApCoeffMod *= 100.0f;
                    modOwner.ApplySpellMod(spellProto.Id, SpellModOp.BonusMultiplier, ref ApCoeffMod);
                    ApCoeffMod /= 100.0f;
                }

                WeaponAttackType attType = (spellProto.IsRangedWeaponSpell() && spellProto.DmgClass != SpellDmgClass.Melee) ? WeaponAttackType.RangedAttack : WeaponAttackType.BaseAttack;
                float APbonus = victim.GetTotalAuraModifier(attType == WeaponAttackType.BaseAttack ? AuraType.MeleeAttackPowerAttackerBonus : AuraType.RangedAttackPowerAttackerBonus);
                APbonus += GetTotalAttackPowerValue(attType);
                DoneTotal += (int)(stack * ApCoeffMod * APbonus);
            }

            // Default calculation
            float coeff = effect.BonusCoefficient;
            if (DoneAdvertisedBenefit != 0)
            {
                float factorMod = CalculateLevelPenalty(spellProto) * stack;
                Player modOwner = GetSpellModOwner();
                if (modOwner)
                {
                    coeff *= 100.0f;
                    modOwner.ApplySpellMod(spellProto.Id, SpellModOp.BonusMultiplier, ref coeff);
                    coeff /= 100.0f;
                }
                DoneTotal += (int)(DoneAdvertisedBenefit * coeff * factorMod);
            }

            // Done Percentage for DOT is already calculated, no need to do it again. The percentage mod is applied in Aura.HandleAuraSpecificMods.
            float tmpDamage = ((int)pdamage + DoneTotal) * (damagetype == DamageEffectType.DOT ? 1.0f : SpellDamagePctDone(victim, spellProto, damagetype));
            // apply spellmod to Done damage (flat and pct)
            Player _modOwner = GetSpellModOwner();
            if (_modOwner)
                _modOwner.ApplySpellMod(spellProto.Id, damagetype == DamageEffectType.DOT ? SpellModOp.Dot : SpellModOp.Damage, ref tmpDamage);

            return (uint)Math.Max(tmpDamage, 0.0f);
        }

        public float SpellDamagePctDone(Unit victim, SpellInfo spellProto, DamageEffectType damagetype)
        {
            if (spellProto == null || !victim || damagetype == DamageEffectType.Direct)
                return 1.0f;

            // Some spells don't benefit from pct done mods
            if (spellProto.HasAttribute(SpellAttr6.NoDonePctDamageMods))
                return 1.0f;

            // For totems pct done mods are calculated when its calculation is run on the player in SpellDamageBonusDone.
            if (IsTypeId(TypeId.Unit) && IsTotem())
                return 1.0f;

            // Done total percent damage auras
            float DoneTotalMod = 1.0f;

            // Pet damage?
            if (IsTypeId(TypeId.Unit) && !IsPet())
                DoneTotalMod *= ToCreature().GetSpellDamageMod(ToCreature().GetCreatureTemplate().Rank);

            float maxModDamagePercentSchool = 0.0f;
            if (IsTypeId(TypeId.Player))
            {
                for (int i = 0; i < (int)SpellSchools.Max; ++i)
                {
                    if (Convert.ToBoolean((int)spellProto.GetSchoolMask() & (1 << i)))
                        maxModDamagePercentSchool = Math.Max(maxModDamagePercentSchool, GetFloatValue(PlayerFields.ModDamageDonePct + i));
                }
            }
            else
                maxModDamagePercentSchool = GetTotalAuraMultiplierByMiscMask(AuraType.ModDamagePercentDone, (uint)spellProto.GetSchoolMask());

            DoneTotalMod *= maxModDamagePercentSchool;

            uint creatureTypeMask = victim.GetCreatureTypeMask();

            var mDamageDoneVersus = GetAuraEffectsByType(AuraType.ModDamageDoneVersus);
            foreach (var eff in mDamageDoneVersus)
            {
                if (creatureTypeMask.HasAnyFlag((uint)eff.GetMiscValue()))
                    MathFunctions.AddPct(ref DoneTotalMod, eff.GetAmount());
            }

            // bonus against aurastate
            var mDamageDoneVersusAurastate = GetAuraEffectsByType(AuraType.ModDamageDoneVersusAurastate);
            foreach (var eff in mDamageDoneVersusAurastate)
            {
                if (victim.HasAuraState((AuraStateType)eff.GetMiscValue()))
                    MathFunctions.AddPct(ref DoneTotalMod, eff.GetAmount());
            }

            // Add SPELL_AURA_MOD_DAMAGE_DONE_FOR_MECHANIC percent bonus
            MathFunctions.AddPct(ref DoneTotalMod, GetTotalAuraModifierByMiscValue(AuraType.ModDamageDoneForMechanic, (int)spellProto.Mechanic));

            // Custom scripted damage
            switch (spellProto.SpellFamilyName)
            {
                case SpellFamilyNames.Mage:
                    // Ice Lance (no unique family flag)
                    if (spellProto.Id == 228598)
                        if (victim.HasAuraState(AuraStateType.Frozen, spellProto, this))
                            DoneTotalMod *= 3.0f;

                    break;
                case SpellFamilyNames.Priest:
                    // Smite
                    if (spellProto.SpellFamilyFlags[0].HasAnyFlag<uint>(0x80))
                    {
                        // Glyph of Smite
                        AuraEffect aurEff = GetAuraEffect(55692, 0);
                        if (aurEff != null)
                            if (victim.GetAuraEffect(AuraType.PeriodicDamage, SpellFamilyNames.Priest, new FlagArray128(0x100000, 0, 0), GetGUID()) != null)
                                MathFunctions.AddPct(ref DoneTotalMod, aurEff.GetAmount());
                    }
                    break;
                case SpellFamilyNames.Warlock:
                    // Shadow Bite (30% increase from each dot)
                    if (spellProto.SpellFamilyFlags[1].HasAnyFlag<uint>(0x00400000) && IsPet())
                    {
                        uint count = victim.GetDoTsByCaster(GetOwnerGUID());
                        if (count != 0)
                            MathFunctions.AddPct(ref DoneTotalMod, 30 * count);
                    }

                    // Drain Soul - increased damage for targets under 25 % HP
                    if (spellProto.SpellFamilyFlags[0].HasAnyFlag<uint>(0x00004000))
                        if (HasAura(100001))
                            DoneTotalMod *= 2;
                    break;
                case SpellFamilyNames.Deathknight:
                    // Sigil of the Vengeful Heart
                    if (spellProto.SpellFamilyFlags[0].HasAnyFlag<uint>(0x2000))
                    {
                        AuraEffect aurEff = GetAuraEffect(64962, 1);
                        if (aurEff != null)
                            DoneTotalMod += aurEff.GetAmount();
                    }
                    break;
            }

            return DoneTotalMod;
        }

        public uint SpellDamageBonusTaken(Unit caster, SpellInfo spellProto, uint pdamage, DamageEffectType damagetype, SpellEffectInfo effect, uint stack = 1)
        {
            if (spellProto == null || damagetype == DamageEffectType.Direct)
                return pdamage;

            int TakenTotal = 0;
            float TakenTotalMod = 1.0f;
            float TakenTotalCasterMod = 0.0f;

            // Mod damage from spell mechanic
            uint mechanicMask = spellProto.GetAllEffectsMechanicMask();
            if (mechanicMask != 0)
            {
                var mDamageDoneMechanic = GetAuraEffectsByType(AuraType.ModMechanicDamageTakenPercent);
                foreach (var eff in mDamageDoneMechanic)
                    if (Convert.ToBoolean(mechanicMask & (1 << eff.GetMiscValue())))
                        MathFunctions.AddPct(ref TakenTotalMod, eff.GetAmount());
            }

            AuraEffect cheatDeath = GetAuraEffect(45182, 0);
            if (cheatDeath != null)
                if (cheatDeath.GetMiscValue().HasAnyFlag((int)SpellSchoolMask.Normal))
                    MathFunctions.AddPct(ref TakenTotalMod, cheatDeath.GetAmount());

            // Spells with SPELL_ATTR4_FIXED_DAMAGE should only benefit from mechanic damage mod auras.
            if (!spellProto.HasAttribute(SpellAttr4.FixedDamage))
            {
                // get all auras from caster that allow the spell to ignore resistance (sanctified wrath)
                var IgnoreResistAuras = caster.GetAuraEffectsByType(AuraType.ModIgnoreTargetResist);
                foreach (var eff in IgnoreResistAuras)
                {
                    if (Convert.ToBoolean(eff.GetMiscValue() & (int)spellProto.GetSchoolMask()))
                        TakenTotalCasterMod += eff.GetAmount();
                }

                // from positive and negative SPELL_AURA_MOD_DAMAGE_PERCENT_TAKEN
                // multiplicative bonus, for example Dispersion + Shadowform (0.10*0.85=0.085)
                TakenTotalMod *= GetTotalAuraMultiplierByMiscMask(AuraType.ModDamagePercentTaken, (uint)spellProto.GetSchoolMask());

                // From caster spells
                var mOwnerTaken = GetAuraEffectsByType(AuraType.ModSpellDamageFromCaster);
                foreach (var eff in mOwnerTaken)
                    if (eff.GetCasterGUID() == caster.GetGUID() && eff.IsAffectingSpell(spellProto))
                        MathFunctions.AddPct(ref TakenTotalMod, eff.GetAmount());

                int TakenAdvertisedBenefit = SpellBaseDamageBonusTaken(spellProto.GetSchoolMask());

                // Check for table values
                float coeff = effect.BonusCoefficient;

                // Default calculation
                if (TakenAdvertisedBenefit != 0)
                {
                    float factorMod = CalculateLevelPenalty(spellProto) * stack;
                    // level penalty still applied on Taken bonus - is it blizzlike?
                    Player modOwner = GetSpellModOwner();
                    if (modOwner)
                    {
                        coeff *= 100.0f;
                        modOwner.ApplySpellMod(spellProto.Id, SpellModOp.BonusMultiplier, ref coeff);
                        coeff /= 100.0f;
                    }
                    TakenTotal += (int)(TakenAdvertisedBenefit * coeff * factorMod);
                }
            }

            float tmpDamage = 0.0f;

            if (TakenTotalCasterMod != 0)
            {
                if (TakenTotal < 0)
                {
                    if (TakenTotalMod < 1)
                        tmpDamage = (((MathFunctions.CalculatePct(pdamage, TakenTotalCasterMod) + TakenTotal) * TakenTotalMod) + MathFunctions.CalculatePct(pdamage, TakenTotalCasterMod));
                    else
                        tmpDamage = (((float)(MathFunctions.CalculatePct(pdamage, TakenTotalCasterMod) + TakenTotal) + MathFunctions.CalculatePct(pdamage, TakenTotalCasterMod)) * TakenTotalMod);
                }
                else if (TakenTotalMod < 1)
                    tmpDamage = ((MathFunctions.CalculatePct(pdamage + TakenTotal, TakenTotalCasterMod) * TakenTotalMod) + MathFunctions.CalculatePct(pdamage + TakenTotal, TakenTotalCasterMod));
            }
            if (tmpDamage == 0)
                tmpDamage = (pdamage + TakenTotal) * TakenTotalMod;

            return (uint)Math.Max(tmpDamage, 0.0f);
        }

        public uint SpellBaseHealingBonusDone(SpellSchoolMask schoolMask)
        {
            if (IsTypeId(TypeId.Player))
            {
                float overrideSP = GetFloatValue(PlayerFields.OverrideSpellPowerByApPct);
                if (overrideSP > 0.0f)
                    return (uint)(MathFunctions.CalculatePct(GetTotalAttackPowerValue(WeaponAttackType.BaseAttack), overrideSP) + 0.5f);
            }

            uint advertisedBenefit = 0;

            var mHealingDone = GetAuraEffectsByType(AuraType.ModHealingDone);
            foreach (var i in mHealingDone)
                if (i.GetMiscValue() == 0 || (i.GetMiscValue() & (int)schoolMask) != 0)
                    advertisedBenefit += (uint)i.GetAmount();

            // Healing bonus of spirit, intellect and strength
            if (IsTypeId(TypeId.Player))
            {
                // Base value
                advertisedBenefit += ToPlayer().GetBaseSpellPowerBonus();

                // Check if we are ever using mana - PaperDollFrame.lua
                if (GetPowerIndex(PowerType.Mana) != (uint)PowerType.Max)
                    advertisedBenefit += Math.Max(0, (uint)GetStat(Stats.Intellect));  // spellpower from intellect

                // Healing bonus from stats
                var mHealingDoneOfStatPercent = GetAuraEffectsByType(AuraType.ModSpellHealingOfStatPercent);
                foreach (var i in mHealingDoneOfStatPercent)
                {
                    // stat used dependent from misc value (stat index)
                    Stats usedStat = (Stats)(i.GetSpellEffectInfo().MiscValue);
                    advertisedBenefit += (uint)MathFunctions.CalculatePct(GetStat(usedStat), i.GetAmount());
                }

                // ... and attack power
                var mHealingDonebyAP = GetAuraEffectsByType(AuraType.ModSpellHealingOfAttackPower);
                foreach (var i in mHealingDonebyAP)
                    if (Convert.ToBoolean(i.GetMiscValue() & (int)schoolMask))
                        advertisedBenefit += (uint)MathFunctions.CalculatePct(GetTotalAttackPowerValue(WeaponAttackType.BaseAttack), i.GetAmount());
            }
            return advertisedBenefit;
        }

        int SpellBaseHealingBonusTaken(SpellSchoolMask schoolMask)
        {
            int advertisedBenefit = 0;

            var mDamageTaken = GetAuraEffectsByType(AuraType.ModHealing);
            foreach (var i in mDamageTaken)
                if ((i.GetMiscValue() & (int)schoolMask) != 0)
                    advertisedBenefit += i.GetAmount();

            return advertisedBenefit;
        }

        public int SpellCriticalHealingBonus(SpellInfo spellProto, int damage, Unit victim)
        {
            // Calculate critical bonus
            int crit_bonus = damage;

            damage += crit_bonus;

            damage = (int)(damage * GetTotalAuraMultiplier(AuraType.ModCriticalHealingAmount));

            return damage;
        }

        public uint SpellHealingBonusDone(Unit victim, SpellInfo spellProto, uint healamount, DamageEffectType damagetype, SpellEffectInfo effect, uint stack = 1)
        {
            // For totems get healing bonus from owner (statue isn't totem in fact)
            if (IsTypeId(TypeId.Unit) && IsTotem())
            {
                Unit owner1 = GetOwner();
                if (owner1)
                    return owner1.SpellHealingBonusDone(victim, spellProto, healamount, damagetype, effect, stack);
            }

            // No bonus healing for potion spells
            if (spellProto.SpellFamilyName == SpellFamilyNames.Potion)
                return healamount;

            int DoneTotal = 0;

            // done scripted mod (take it from owner)
            Unit owner = GetOwner() ?? this;
            var mOverrideClassScript = owner.GetAuraEffectsByType(AuraType.OverrideClassScripts);
            foreach (var eff in mOverrideClassScript)
            {
                if (!eff.IsAffectingSpell(spellProto))
                    continue;

                switch (eff.GetMiscValue())
                {
                    case 3736: // Hateful Totem of the Third Wind / Increased Lesser Healing Wave / LK Arena (4/5/6) Totem of the Third Wind / Savage Totem of the Third Wind
                        DoneTotal += eff.GetAmount();
                        break;
                    default:
                        break;
                }
            }

            // Done fixed damage bonus auras
            uint DoneAdvertisedBenefit = SpellBaseHealingBonusDone(spellProto.GetSchoolMask());

            // Check for table values
            float coeff = effect.BonusCoefficient;
            float factorMod = 1.0f;
            if (effect.BonusCoefficientFromAP > 0.0f)
            {
                DoneTotal += (int)(effect.BonusCoefficientFromAP * stack * GetTotalAttackPowerValue(
                    (spellProto.IsRangedWeaponSpell() && spellProto.DmgClass != SpellDmgClass.Melee) ? WeaponAttackType.RangedAttack : WeaponAttackType.BaseAttack));
            }
            else if (coeff <= 0.0f)
            {
                // No bonus healing for SPELL_DAMAGE_CLASS_NONE class spells by default
                if (spellProto.DmgClass == SpellDmgClass.None)
                    return healamount;
            }

            // Default calculation
            if (DoneAdvertisedBenefit != 0)
            {
                factorMod *= CalculateLevelPenalty(spellProto) * stack;
                Player modOwner = GetSpellModOwner();
                if (modOwner)
                {
                    coeff *= 100.0f;
                    modOwner.ApplySpellMod(spellProto.Id, SpellModOp.BonusMultiplier, ref coeff);
                    coeff /= 100.0f;
                }

                DoneTotal += (int)(DoneAdvertisedBenefit * coeff * factorMod);
            }

            foreach (SpellEffectInfo eff in spellProto.GetEffectsForDifficulty(GetMap().GetDifficultyID()))
            {
                if (eff == null)
                    continue;

                switch (eff.ApplyAuraName)
                {
                    // Bonus healing does not apply to these spells
                    case AuraType.PeriodicLeech:
                    case AuraType.PeriodicHealthFunnel:
                        DoneTotal = 0;
                        break;
                }
                if (eff.Effect == SpellEffectName.HealthLeech)
                    DoneTotal = 0;
            }

            // use float as more appropriate for negative values and percent applying
            float heal = (healamount + DoneTotal) * (damagetype == DamageEffectType.DOT ? 1.0f : SpellHealingPctDone(victim, spellProto));
            // apply spellmod to Done amount
            Player _modOwner = GetSpellModOwner();
            if (_modOwner)
                _modOwner.ApplySpellMod(spellProto.Id, damagetype == DamageEffectType.DOT ? SpellModOp.Dot : SpellModOp.Damage, ref heal);

            return (uint)Math.Max(heal, 0.0f);
        }

        public float SpellHealingPctDone(Unit victim, SpellInfo spellProto)
        {
            // For totems pct done mods are calculated when its calculation is run on the player in SpellHealingBonusDone.
            if (IsTypeId(TypeId.Unit) && IsTotem())
                return 1.0f;

            // No bonus healing for potion spells
            if (spellProto.SpellFamilyName == SpellFamilyNames.Potion)
                return 1.0f;

            float DoneTotalMod = 1.0f;

            // Healing done percent
            var mHealingDonePct = GetAuraEffectsByType(AuraType.ModHealingDonePercent);
            foreach (var eff in mHealingDonePct)
                MathFunctions.AddPct(ref DoneTotalMod, eff.GetAmount());

            return DoneTotalMod;
        }

        public uint SpellHealingBonusTaken(Unit caster, SpellInfo spellProto, uint healamount, DamageEffectType damagetype, SpellEffectInfo effect, uint stack = 1)
        {
            float TakenTotalMod = 1.0f;

            // Healing taken percent
            float minval = GetMaxNegativeAuraModifier(AuraType.ModHealingPct);
            if (minval != 0)
                MathFunctions.AddPct(ref TakenTotalMod, minval);

            float maxval = GetMaxPositiveAuraModifier(AuraType.ModHealingPct);
            if (maxval != 0)
                MathFunctions.AddPct(ref TakenTotalMod, maxval);

            // Tenacity increase healing % taken
            AuraEffect Tenacity = GetAuraEffect(58549, 0);
            if (Tenacity != null)
                MathFunctions.AddPct(ref TakenTotalMod, Tenacity.GetAmount());

            // Healing Done
            int TakenTotal = 0;

            // Taken fixed damage bonus auras
            int TakenAdvertisedBenefit = SpellBaseHealingBonusTaken(spellProto.GetSchoolMask());

            // Nourish cast
            if (spellProto.SpellFamilyName == SpellFamilyNames.Druid && spellProto.SpellFamilyFlags[1].HasAnyFlag(0x2000000u))
            {
                // Rejuvenation, Regrowth, Lifebloom, or Wild Growth
                if (GetAuraEffect(AuraType.PeriodicHeal, SpellFamilyNames.Druid, new FlagArray128(0x50, 0x4000010, 0)) != null)
                    // increase healing by 20%
                    TakenTotalMod *= 1.2f;
            }

            // Check for table values
            float coeff = effect.BonusCoefficient;
            float factorMod = 1.0f;
            if (coeff <= 0.0f)
            {
                // No bonus healing for SPELL_DAMAGE_CLASS_NONE class spells by default
                if (spellProto.DmgClass == SpellDmgClass.None)
                {
                    healamount = (uint)Math.Max((healamount * TakenTotalMod), 0.0f);
                    return healamount;
                }
            }

            // Default calculation
            if (TakenAdvertisedBenefit != 0)
            {
                factorMod *= CalculateLevelPenalty(spellProto) * stack;
                Player modOwner = GetSpellModOwner();
                if (modOwner)
                {
                    coeff *= 100.0f;
                    modOwner.ApplySpellMod(spellProto.Id, SpellModOp.BonusMultiplier, ref coeff);
                    coeff /= 100.0f;
                }

                TakenTotal += (int)(TakenAdvertisedBenefit * coeff * factorMod);
            }

            var mHealingGet = GetAuraEffectsByType(AuraType.ModHealingReceived);
            foreach (var eff in mHealingGet)
                if (caster.GetGUID() == eff.GetCasterGUID() && eff.IsAffectingSpell(spellProto))
                    MathFunctions.AddPct(ref TakenTotalMod, eff.GetAmount());

            foreach (SpellEffectInfo eff in spellProto.GetEffectsForDifficulty(GetMap().GetDifficultyID()))
            {
                if (eff == null)
                    continue;

                switch (eff.ApplyAuraName)
                {
                    // Bonus healing does not apply to these spells
                    case AuraType.PeriodicLeech:
                    case AuraType.PeriodicHealthFunnel:
                        TakenTotal = 0;
                        break;
                }
                if (eff.Effect == SpellEffectName.HealthLeech)
                    TakenTotal = 0;
            }

            float heal = (healamount + TakenTotal) * TakenTotalMod;

            return (uint)Math.Max(heal, 0.0f);
        }

        public bool IsSpellCrit(Unit victim, SpellInfo spellProto, SpellSchoolMask schoolMask, WeaponAttackType attackType = WeaponAttackType.BaseAttack)
        {
            return RandomHelper.randChance(GetUnitSpellCriticalChance(victim, spellProto, schoolMask, attackType));
        }

        public float GetUnitSpellCriticalChance(Unit victim, SpellInfo spellProto, SpellSchoolMask schoolMask, WeaponAttackType attackType = WeaponAttackType.BaseAttack)
        {
            //! Mobs can't crit with spells. Player Totems can
            //! Fire Elemental (from totem) can too - but this part is a hack and needs more research
            if (GetGUID().IsCreatureOrVehicle() && !(IsTotem() && GetOwnerGUID().IsPlayer()) && GetEntry() != 15438)
                return 0.0f;

            // not critting spell
            if (spellProto.HasAttribute(SpellAttr2.CantCrit))
                return 0.0f;

            float crit_chance = 0.0f;
            switch (spellProto.DmgClass)
            {
                case SpellDmgClass.None:
                    // We need more spells to find a general way (if there is any)
                    switch (spellProto.Id)
                    {
                        case 379:   // Earth Shield
                        case 33778: // Lifebloom Final Bloom
                        case 64844: // Divine Hymn
                        case 71607: // Item - Bauble of True Blood 10m
                        case 71646: // Item - Bauble of True Blood 25m
                            break;
                        default:
                            return 0.0f;
                    }
                    goto case SpellDmgClass.Magic;
                case SpellDmgClass.Magic:
                    {
                        if (schoolMask.HasAnyFlag(SpellSchoolMask.Normal))
                            crit_chance = 0.0f;
                        // For other schools
                        else if (IsTypeId(TypeId.Player))
                            crit_chance = GetFloatValue(PlayerFields.CritPercentage);
                        else
                            crit_chance = m_baseSpellCritChance;

                        // taken
                        if (victim)
                        {
                            if (!spellProto.IsPositive())
                            {
                                // Modify critical chance by victim SPELL_AURA_MOD_ATTACKER_SPELL_AND_WEAPON_CRIT_CHANCE
                                crit_chance += victim.GetTotalAuraModifier(AuraType.ModAttackerSpellAndWeaponCritChance);
                            }
                            // scripted (increase crit chance ... against ... target by x%
                            var mOverrideClassScript = GetAuraEffectsByType(AuraType.OverrideClassScripts);
                            foreach (var eff in mOverrideClassScript)
                            {
                                if (!eff.IsAffectingSpell(spellProto))
                                    continue;

                                switch (eff.GetMiscValue())
                                {
                                    case 911: // Shatter
                                        if (victim.HasAuraState(AuraStateType.Frozen, spellProto, this))
                                        {
                                            crit_chance *= 1.5f;
                                            AuraEffect _eff = eff.GetBase().GetEffect(1);
                                            if (_eff != null)
                                                crit_chance += _eff.GetAmount();
                                        }
                                        break;
                                    default:
                                        break;
                                }
                            }
                            // Custom crit by class
                            switch (spellProto.SpellFamilyName)
                            {
                                case SpellFamilyNames.Rogue:
                                    // Shiv-applied poisons can't crit
                                    if (FindCurrentSpellBySpellId(5938) != null)
                                        crit_chance = 0.0f;
                                    break;
                                case SpellFamilyNames.Paladin:
                                    // Flash of light
                                    if (spellProto.SpellFamilyFlags[0].HasAnyFlag(0x40000000u))
                                    {
                                        // Sacred Shield
                                        AuraEffect aura = victim.GetAuraEffect(58597, 1, GetGUID());
                                        if (aura != null)
                                            crit_chance += aura.GetAmount();
                                        break;
                                    }
                                    // Exorcism
                                    else if (spellProto.GetCategory() == 19)
                                    {
                                        if (victim.GetCreatureTypeMask().HasAnyFlag((uint)CreatureType.MaskDemonOrUnDead))
                                            return 100.0f;
                                        break;
                                    }
                                    break;
                                case SpellFamilyNames.Shaman:
                                    // Lava Burst
                                    if (spellProto.SpellFamilyFlags[1].HasAnyFlag(0x00001000u))
                                    {
                                        if (victim.GetAuraEffect(AuraType.PeriodicDamage, SpellFamilyNames.Shaman, new FlagArray128(0x10000000, 0, 0), GetGUID()) != null)
                                            if (victim.GetTotalAuraModifier(AuraType.ModAttackerSpellAndWeaponCritChance) > -100)
                                                return 100.0f;
                                        break;
                                    }
                                    break;
                            }
                        }
                        break;
                    }
                case SpellDmgClass.Melee:
                case SpellDmgClass.Ranged:
                    {
                        if (victim)
                            crit_chance += GetUnitCriticalChance(attackType, victim);
                        break;
                    }
                default:
                    return 0.0f;
            }
            // percent done
            // only players use intelligence for critical chance computations
            Player modOwner = GetSpellModOwner();
            if (modOwner != null)
                modOwner.ApplySpellMod(spellProto.Id, SpellModOp.CriticalChance, ref crit_chance);

            var critChanceForCaster = victim.GetAuraEffectsByType(AuraType.ModCritChanceForCaster);
            foreach (AuraEffect aurEff in critChanceForCaster)
                if (aurEff.GetCasterGUID() == GetGUID() && aurEff.IsAffectingSpell(spellProto))
                    crit_chance += aurEff.GetAmount();

            return crit_chance > 0.0f ? crit_chance : 0.0f;
        }

        // Calculate spell hit result can be:
        // Every spell can: Evade/Immune/Reflect/Sucesful hit
        // For melee based spells:
        //   Miss
        //   Dodge
        //   Parry
        // For spells
        //   Resist
        public SpellMissInfo SpellHitResult(Unit victim, SpellInfo spell, bool CanReflect)
        {
            // Check for immune
            if (victim.IsImmunedToSpell(spell))
                return SpellMissInfo.Immune;

            // All positive spells can`t miss
            // @todo client not show miss log for this spells - so need find info for this in dbc and use it!
            if (spell.IsPositive()
                && (!IsHostileTo(victim)))  // prevent from affecting enemy by "positive" spell
                return SpellMissInfo.None;
            // Check for immune
            if (victim.IsImmunedToDamage(spell))
                return SpellMissInfo.Immune;

            if (this == victim)
                return SpellMissInfo.None;

            // Return evade for units in evade mode
            if (victim.IsTypeId(TypeId.Unit) && victim.ToCreature().IsEvadingAttacks())
                return SpellMissInfo.Evade;

            // Try victim reflect spell
            if (CanReflect)
            {
                int reflectchance = victim.GetTotalAuraModifier(AuraType.ReflectSpells);
                var mReflectSpellsSchool = victim.GetAuraEffectsByType(AuraType.ReflectSpellsSchool);
                foreach (var eff in mReflectSpellsSchool)
                    if (Convert.ToBoolean(eff.GetMiscValue() & (int)spell.GetSchoolMask()))
                        reflectchance += eff.GetAmount();
                if (reflectchance > 0 && RandomHelper.randChance(reflectchance))
                {
                    // Start triggers for remove charges if need (trigger only for victim, and mark as active spell)
                    ProcDamageAndSpell(victim, ProcFlags.None, ProcFlags.TakenSpellMagicDmgClassNeg, ProcFlagsExLegacy.Reflect, 1, WeaponAttackType.BaseAttack, spell);
                    return SpellMissInfo.Reflect;
                }
            }

            switch (spell.DmgClass)
            {
                case SpellDmgClass.Ranged:
                case SpellDmgClass.Melee:
                    return MeleeSpellHitResult(victim, spell);
                case SpellDmgClass.None:
                    return SpellMissInfo.None;
                case SpellDmgClass.Magic:
                    return MagicSpellHitResult(victim, spell);
            }
            return SpellMissInfo.None;
        }

        // Melee based spells hit result calculations
        SpellMissInfo MeleeSpellHitResult(Unit victim, SpellInfo spellInfo)
        {
            // Spells with SPELL_ATTR3_IGNORE_HIT_RESULT will additionally fully ignore
            // resist and deflect chances
            if (spellInfo.HasAttribute(SpellAttr3.IgnoreHitResult))
                return SpellMissInfo.None;

            WeaponAttackType attType = WeaponAttackType.BaseAttack;

            // Check damage class instead of attack type to correctly handle judgements
            // - they are meele, but can't be dodged/parried/deflected because of ranged dmg class
            if (spellInfo.DmgClass == SpellDmgClass.Ranged)
                attType = WeaponAttackType.RangedAttack;

            int roll = RandomHelper.IRand(0, 9999);

            int missChance = (int)(MeleeSpellMissChance(victim, attType, spellInfo.Id) * 100.0f);
            // Roll miss
            int tmp = missChance;
            if (roll < tmp)
                return SpellMissInfo.Miss;

            // Chance resist mechanic
            int resist_chance = victim.GetMechanicResistChance(spellInfo) * 100;
            tmp += resist_chance;
            if (roll < tmp)
                return SpellMissInfo.Resist;

            // Same spells cannot be parried/dodged
            if (spellInfo.HasAttribute(SpellAttr0.ImpossibleDodgeParryBlock))
                return SpellMissInfo.None;

            bool canDodge = true;
            bool canParry = true;
            bool canBlock = spellInfo.HasAttribute(SpellAttr3.BlockableSpell);

            // if victim is casting or cc'd it can't avoid attacks
            if (victim.IsNonMeleeSpellCast(false) || victim.HasUnitState(UnitState.Controlled))
            {
                canDodge = false;
                canParry = false;
                canBlock = false;
            }

            // Ranged attacks can only miss, resist and deflect
            if (attType == WeaponAttackType.RangedAttack)
            {
                canParry = false;
                canDodge = false;

                // only if in front
                if (victim.HasInArc(MathFunctions.PI, this) || victim.HasAuraType(AuraType.IgnoreHitDirection))
                {
                    int deflect_chance = victim.GetTotalAuraModifier(AuraType.DeflectSpells) * 100;
                    tmp += deflect_chance;
                    if (roll < tmp)
                        return SpellMissInfo.Deflect;
                }
                return SpellMissInfo.None;
            }

            // Check for attack from behind
            if (!victim.HasInArc(MathFunctions.PI, this))
            {
                if (!victim.HasAuraType(AuraType.IgnoreHitDirection))
                {
                    // Can`t dodge from behind in PvP (but its possible in PvE)
                    if (victim.IsTypeId(TypeId.Player))
                        canDodge = false;
                    // Can`t parry or block
                    canParry = false;
                    canBlock = false;
                }
                else // Only deterrence as of 3.3.5
                {
                    if (spellInfo.HasAttribute(SpellCustomAttributes.ReqCasterBehindTarget))
                        canParry = false;
                }
            }

            // Ignore combat result aura
            var ignore = GetAuraEffectsByType(AuraType.IgnoreCombatResult);
            foreach (var aurEff in ignore)
            {
                if (!aurEff.IsAffectingSpell(spellInfo))
                    continue;

                switch ((MeleeHitOutcome)aurEff.GetMiscValue())
                {
                    case MeleeHitOutcome.Dodge:
                        canDodge = false;
                        break;
                    case MeleeHitOutcome.Block:
                        canBlock = false;
                        break;
                    case MeleeHitOutcome.Parry:
                        canParry = false;
                        break;
                    default:
                        Log.outDebug(LogFilter.Unit, "Spell {0} SPELL_AURA_IGNORE_COMBAT_RESULT has unhandled state {1}", aurEff.GetId(), aurEff.GetMiscValue());
                        break;
                }
            }

            if (canDodge)
            {
                // Roll dodge
                int dodgeChance = (int)(GetUnitDodgeChance(attType, victim) * 100.0f);
                if (dodgeChance < 0)
                    dodgeChance = 0;

                if (roll < (tmp += dodgeChance))
                    return SpellMissInfo.Dodge;
            }

            if (canParry)
            {
                // Roll parry
                int parryChance = (int)(GetUnitParryChance(attType, victim) * 100.0f);
                if (parryChance < 0)
                    parryChance = 0;

                tmp += parryChance;
                if (roll < tmp)
                    return SpellMissInfo.Parry;
            }

            if (canBlock)
            {
                int blockChance = (int)(GetUnitBlockChance(attType, victim) * 100.0f);
                if (blockChance < 0)
                    blockChance = 0;
                tmp += blockChance;

                if (roll < tmp)
                    return SpellMissInfo.Block;
            }

            return SpellMissInfo.None;
        }

        // @todo need use unit spell resistances in calculations
        SpellMissInfo MagicSpellHitResult(Unit victim, SpellInfo spell)
        {
            // Can`t miss on dead target (on skinning for example)
            if (!victim.IsAlive() && !victim.IsTypeId(TypeId.Player))
                return SpellMissInfo.None;

            SpellSchoolMask schoolMask = spell.GetSchoolMask();
            // PvP - PvE spell misschances per leveldif > 2
            int lchance = victim.IsTypeId(TypeId.Player) ? 7 : 11;
            int thisLevel = (int)GetLevelForTarget(victim);
            if (IsTypeId(TypeId.Unit) && ToCreature().IsTrigger())
                thisLevel = (int)Math.Max(thisLevel, spell.SpellLevel);
            int leveldif = (int)(victim.GetLevelForTarget(this)) - thisLevel;
            int levelBasedHitDiff = leveldif;

            // Base hit chance from attacker and victim levels
            int modHitChance = 100;
            if (levelBasedHitDiff >= 0)
            {
                if (!victim.IsTypeId(TypeId.Player))
                {
                    modHitChance = 94 - 3 * Math.Min(levelBasedHitDiff, 3);
                    levelBasedHitDiff -= 3;
                }
                else
                {
                    modHitChance = 96 - Math.Min(levelBasedHitDiff, 2);
                    levelBasedHitDiff -= 2;
                }
                if (levelBasedHitDiff > 0)
                    modHitChance -= lchance * Math.Min(levelBasedHitDiff, 7);
            }
            else
                modHitChance = 97 - levelBasedHitDiff;

            // Spellmod from SPELLMOD_RESIST_MISS_CHANCE
            Player modOwner = GetSpellModOwner();
            if (modOwner != null)
                modOwner.ApplySpellMod(spell.Id, SpellModOp.ResistMissChance, ref modHitChance);

            // Spells with SPELL_ATTR3_IGNORE_HIT_RESULT will ignore target's avoidance effects
            if (!spell.HasAttribute(SpellAttr3.IgnoreHitResult))
            {
                // Chance hit from victim SPELL_AURA_MOD_ATTACKER_SPELL_HIT_CHANCE auras
                modHitChance += victim.GetTotalAuraModifierByMiscMask(AuraType.ModAttackerSpellHitChance, (int)schoolMask);
            }

            int HitChance = modHitChance * 100;
            // Increase hit chance from attacker SPELL_AURA_MOD_SPELL_HIT_CHANCE and attacker ratings
            HitChance += (int)(modHitChance * 100.0f);

            if (HitChance < 100)
                HitChance = 100;
            else if (HitChance > 10000)
                HitChance = 10000;

            int tmp = 10000 - HitChance;

            int rand = RandomHelper.IRand(0, 10000);

            if (rand < tmp)
                return SpellMissInfo.Miss;

            // Spells with SPELL_ATTR3_IGNORE_HIT_RESULT will additionally fully ignore
            // resist and deflect chances
            if (spell.HasAttribute(SpellAttr3.IgnoreHitResult))
                return SpellMissInfo.None;

            // Chance resist mechanic (select max value from every mechanic spell effect)
            int resist_chance = victim.GetMechanicResistChance(spell) * 100;
            tmp += resist_chance;

            // Roll chance
            if (rand < tmp)
                return SpellMissInfo.Resist;

            // cast by caster in front of victim
            if (!victim.HasUnitState(UnitState.Controlled) && (victim.HasInArc(MathFunctions.PI, this) || victim.HasAuraType(AuraType.IgnoreHitDirection)))
            {
                int deflect_chance = victim.GetTotalAuraModifier(AuraType.DeflectSpells) * 100;
                tmp += deflect_chance;
                if (rand < tmp)
                    return SpellMissInfo.Deflect;
            }

            return SpellMissInfo.None;
        }

        public void CastSpell(SpellCastTargets targets, SpellInfo spellInfo, Dictionary<SpellValueMod, int> values, TriggerCastFlags triggerFlags = TriggerCastFlags.None, Item castItem = null, AuraEffect triggeredByAura = null, ObjectGuid originalCaster = default(ObjectGuid))
        {
            if (spellInfo == null)
            {
                Log.outError(LogFilter.Spells, "CastSpell: unknown spell by caster: {0}", GetGUID().ToString());
                return;
            }

            Spell spell = new Spell(this, spellInfo, triggerFlags, originalCaster);

            if (values != null)
                foreach (var pair in values)
                    spell.SetSpellValue(pair.Key, pair.Value);

            spell.m_CastItem = castItem;
            spell.prepare(targets, triggeredByAura);
        }
        public void CastSpell(Unit victim, uint spellId, bool triggered, Item castItem = null, AuraEffect triggeredByAura = null, ObjectGuid originalCaster = default(ObjectGuid))
        {
            CastSpell(victim, spellId, triggered ? TriggerCastFlags.FullMask : TriggerCastFlags.None, castItem, triggeredByAura, originalCaster);
        }
        public void CastSpell(Unit victim, uint spellId, TriggerCastFlags triggerFlags = TriggerCastFlags.None, Item castItem = null, AuraEffect triggeredByAura = null, ObjectGuid originalCaster = default(ObjectGuid))
        {
            SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(spellId);
            if (spellInfo == null)
            {
                Log.outError(LogFilter.Spells, "CastSpell: unknown spell id {0} by caster: {1}", spellId, GetGUID().ToString());
                return;
            }

            CastSpell(victim, spellInfo, triggerFlags, castItem, triggeredByAura, originalCaster);
        }
        public void CastSpell(Unit victim, SpellInfo spellInfo, bool triggered, Item castItem = null, AuraEffect triggeredByAura = null, ObjectGuid originalCaster = default(ObjectGuid))
        {
            CastSpell(victim, spellInfo, triggered ? TriggerCastFlags.FullMask : TriggerCastFlags.None, castItem, triggeredByAura, originalCaster);
        }
        public void CastSpell(Unit victim, SpellInfo spellInfo, TriggerCastFlags triggerFlags = TriggerCastFlags.None, Item castItem = null, AuraEffect triggeredByAura = null, ObjectGuid originalCaster = default(ObjectGuid))
        {
            SpellCastTargets targets = new SpellCastTargets();
            targets.SetUnitTarget(victim);
            CastSpell(targets, spellInfo, null, triggerFlags, castItem, triggeredByAura, originalCaster);
        }
        public void CastSpell(float x, float y, float z, uint spellId, bool triggered, Item castItem = null, AuraEffect triggeredByAura = null, ObjectGuid originalCaster = default(ObjectGuid))
        {
            SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(spellId);
            if (spellInfo == null)
            {
                Log.outError(LogFilter.Unit, "CastSpell: unknown spell id {0} by caster: {1}", spellId, GetGUID().ToString());
                return;
            }
            SpellCastTargets targets = new SpellCastTargets();
            targets.SetDst(x, y, z, GetOrientation());

            CastSpell(targets, spellInfo, null, triggered ? TriggerCastFlags.FullMask : TriggerCastFlags.None, castItem, triggeredByAura, originalCaster);
        }
        public void CastSpell(GameObject go, uint spellId, bool triggered, Item castItem = null, AuraEffect triggeredByAura = null, ObjectGuid originalCaster = default(ObjectGuid))
        {
            SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(spellId);
            if (spellInfo == null)
            {
                Log.outError(LogFilter.Unit, "CastSpell: unknown spell id {0} by caster: {1}", spellId, GetGUID().ToString());
                return;
            }
            SpellCastTargets targets = new SpellCastTargets();
            targets.SetGOTarget(go);

            CastSpell(targets, spellInfo, null, triggered ? TriggerCastFlags.FullMask : TriggerCastFlags.None, castItem, triggeredByAura, originalCaster);
        }

        public void CastCustomSpell(Unit target, uint spellId, int bp0, int bp1, int bp2, bool triggered, Item castItem = null, AuraEffect triggeredByAura = null, ObjectGuid originalCaster = default(ObjectGuid))
        {
            Dictionary<SpellValueMod, int> values = new Dictionary<SpellValueMod, int>();
            if (bp0 != 0)
                values.Add(SpellValueMod.BasePoint0, bp0);
            if (bp1 != 0)
                values.Add(SpellValueMod.BasePoint1, bp1);
            if (bp2 != 0)
                values.Add(SpellValueMod.BasePoint2, bp2);
            CastCustomSpell(spellId, values, target, triggered ? TriggerCastFlags.FullMask : TriggerCastFlags.None, castItem, triggeredByAura, originalCaster);
        }
        public void CastCustomSpell(uint spellId, SpellValueMod mod, int value, Unit target, bool triggered, Item castItem = null, AuraEffect triggeredByAura = null, ObjectGuid originalCaster = default(ObjectGuid))
        {
            Dictionary<SpellValueMod, int> values = new Dictionary<SpellValueMod, int>();
            values.Add(mod, value);
            CastCustomSpell(spellId, values, target, triggered ? TriggerCastFlags.FullMask : TriggerCastFlags.None, castItem, triggeredByAura, originalCaster);
        }
        public void CastCustomSpell(uint spellId, SpellValueMod mod, int value, Unit target = null, TriggerCastFlags triggerFlags = TriggerCastFlags.None, Item castItem = null, AuraEffect triggeredByAura = null, ObjectGuid originalCaster = default(ObjectGuid))
        {
            Dictionary<SpellValueMod, int> values = new Dictionary<SpellValueMod, int>();
            values.Add(mod, value);
            CastCustomSpell(spellId, values, target, triggerFlags, castItem, triggeredByAura, originalCaster);
        }
        public void CastCustomSpell(uint spellId, Dictionary<SpellValueMod, int> values, Unit victim = null, TriggerCastFlags triggerFlags = TriggerCastFlags.None, Item castItem = null, AuraEffect triggeredByAura = null, ObjectGuid originalCaster = default(ObjectGuid))
        {
            SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(spellId);
            if (spellInfo == null)
            {
                Log.outError(LogFilter.Unit, "CastSpell: unknown spell id {0} by caster: {1}", spellId, GetGUID().ToString());
                return;
            }
            SpellCastTargets targets = new SpellCastTargets();
            targets.SetUnitTarget(victim);

            CastSpell(targets, spellInfo, values, triggerFlags, castItem, triggeredByAura, originalCaster);
        }

        public void FinishSpell(CurrentSpellTypes spellType, bool ok = true)
        {
            Spell spell = GetCurrentSpell(spellType);
            if (spell == null)
                return;

            if (spellType == CurrentSpellTypes.Channeled)
                spell.SendChannelUpdate(0);

            spell.finish(ok);
        }

        public float CalculateLevelPenalty(SpellInfo spellProto)
        {
            if (spellProto.SpellLevel <= 0 || spellProto.SpellLevel >= spellProto.MaxLevel)
                return 1.0f;

            float LvlPenalty = 0.0f;

            if (spellProto.SpellLevel < 20)
                LvlPenalty = (20.0f - spellProto.SpellLevel) * 3.75f;
            float LvlFactor = (spellProto.SpellLevel + 6.0f) / getLevel();
            if (LvlFactor > 1.0f)
                LvlFactor = 1.0f;

            return MathFunctions.AddPct(ref LvlFactor, -LvlPenalty);
        }

        uint GetCastingTimeForBonus(SpellInfo spellProto, DamageEffectType damagetype, uint CastingTime)
        {
            // Not apply this to creature casted spells with casttime == 0
            if (CastingTime == 0 && IsTypeId(TypeId.Unit) && !IsPet())
                return 3500;

            if (CastingTime > 7000) CastingTime = 7000;
            if (CastingTime < 1500) CastingTime = 1500;

            if (damagetype == DamageEffectType.DOT && !spellProto.IsChanneled())
                CastingTime = 3500;

            int overTime = 0;
            byte effects = 0;
            bool DirectDamage = false;
            bool AreaEffect = false;

            foreach (SpellEffectInfo effect in spellProto.GetEffectsForDifficulty(GetMap().GetDifficultyID()))
            {
                if (effect == null)
                    continue;

                switch (effect.Effect)
                {
                    case SpellEffectName.SchoolDamage:
                    case SpellEffectName.PowerDrain:
                    case SpellEffectName.HealthLeech:
                    case SpellEffectName.EnvironmentalDamage:
                    case SpellEffectName.PowerBurn:
                    case SpellEffectName.Heal:
                        DirectDamage = true;
                        break;
                    case SpellEffectName.ApplyAura:
                        switch (effect.ApplyAuraName)
                        {
                            case AuraType.PeriodicDamage:
                            case AuraType.PeriodicHeal:
                            case AuraType.PeriodicLeech:
                                if (spellProto.GetDuration() != 0)
                                    overTime = spellProto.GetDuration();
                                break;
                            default:
                                // -5% per additional effect
                                ++effects;
                                break;
                        }
                        break;
                    default:
                        break;
                }

                if (effect.IsTargetingArea())
                    AreaEffect = true;
            }

            // Combined Spells with Both Over Time and Direct Damage
            if (overTime > 0 && CastingTime > 0 && DirectDamage)
            {
                // mainly for DoTs which are 3500 here otherwise
                int OriginalCastTime = spellProto.CalcCastTime();
                if (OriginalCastTime > 7000) OriginalCastTime = 7000;
                if (OriginalCastTime < 1500) OriginalCastTime = 1500;
                // Portion to Over Time
                float PtOT = (overTime / 15000.0f) / ((overTime / 15000.0f) + (OriginalCastTime / 3500.0f));

                if (damagetype == DamageEffectType.DOT)
                    CastingTime = (uint)(CastingTime * PtOT);
                else if (PtOT < 1.0f)
                    CastingTime = (uint)(CastingTime * (1 - PtOT));
                else
                    CastingTime = 0;
            }

            // Area Effect Spells receive only half of bonus
            if (AreaEffect)
                CastingTime /= 2;

            // 50% for damage and healing spells for leech spells from damage bonus and 0% from healing
            foreach (SpellEffectInfo effect in spellProto.GetEffectsForDifficulty(GetMap().GetDifficultyID()))
            {
                if (effect != null && (effect.Effect == SpellEffectName.HealthLeech ||
                    (effect.Effect == SpellEffectName.ApplyAura && effect.ApplyAuraName == AuraType.PeriodicLeech)))
                {
                    CastingTime /= 2;
                    break;
                }
            }

            // -5% of total per any additional effect
            for (byte i = 0; i < effects; ++i)
                CastingTime *= (uint)0.95f;

            return CastingTime;
        }

        public virtual SpellInfo GetCastSpellInfo(SpellInfo spellInfo)
        {
            var swaps = GetAuraEffectsByType(AuraType.OverrideActionbarSpells);
            var swaps2 = GetAuraEffectsByType(AuraType.OverrideActionbarSpellsTriggered);
            if (!swaps2.Empty())
                swaps.AddRange(swaps2);

            foreach (AuraEffect auraEffect in swaps)
            {
                if (auraEffect.GetMiscValue() == spellInfo.Id || auraEffect.IsAffectingSpell(spellInfo))
                {
                    SpellInfo newInfo = Global.SpellMgr.GetSpellInfo((uint)auraEffect.GetAmount());
                    if (newInfo != null)
                        return newInfo;
                }
            }

            return spellInfo;
        }

        public uint GetCastSpellXSpellVisualId(SpellInfo spellInfo)
        {
            var visualOverrides = GetAuraEffectsByType(AuraType.OverrideSpellVisual);
            foreach (AuraEffect effect in visualOverrides)
            {
                if (effect.GetMiscValue() == spellInfo.Id)
                {
                    SpellInfo visualSpell = Global.SpellMgr.GetSpellInfo((uint)effect.GetMiscValueB());
                    if (visualSpell != null)
                    {
                        spellInfo = visualSpell;
                        break;
                    }
                }
            }

            return spellInfo.GetSpellXSpellVisualId(this);
        }

        public SpellHistory GetSpellHistory() { return _spellHistory; }

        public static ProcFlagsExLegacy createProcExtendMask(SpellNonMeleeDamage damageInfo, SpellMissInfo missCondition)
        {
            ProcFlagsExLegacy procEx = ProcFlagsExLegacy.None;
            // Check victim state
            if (missCondition != SpellMissInfo.None)
                switch (missCondition)
                {
                    case SpellMissInfo.Miss:
                        procEx |= ProcFlagsExLegacy.Miss;
                        break;
                    case SpellMissInfo.Resist:
                        procEx |= ProcFlagsExLegacy.Resist;
                        break;
                    case SpellMissInfo.Dodge:
                        procEx |= ProcFlagsExLegacy.Dodge;
                        break;
                    case SpellMissInfo.Parry:
                        procEx |= ProcFlagsExLegacy.Parry;
                        break;
                    case SpellMissInfo.Block:
                        procEx |= ProcFlagsExLegacy.Block;
                        break;
                    case SpellMissInfo.Evade:
                        procEx |= ProcFlagsExLegacy.Evade;
                        break;
                    case SpellMissInfo.Immune:
                        procEx |= ProcFlagsExLegacy.Immune;
                        break;
                    case SpellMissInfo.Deflect:
                        procEx |= ProcFlagsExLegacy.Deflect;
                        break;
                    case SpellMissInfo.Absorb:
                        procEx |= ProcFlagsExLegacy.Absorb;
                        break;
                    case SpellMissInfo.Reflect:
                        procEx |= ProcFlagsExLegacy.Reflect;
                        break;
                    default:
                        break;
                }
            else
            {
                // On block
                if (damageInfo.blocked != 0)
                    procEx |= ProcFlagsExLegacy.Block;
                // On absorb
                if (damageInfo.absorb != 0)
                    procEx |= ProcFlagsExLegacy.Absorb;
                // On crit
                if (damageInfo.HitInfo.HasAnyFlag(SpellHitType.Crit))
                    procEx |= ProcFlagsExLegacy.CriticalHit;
                else
                    procEx |= ProcFlagsExLegacy.NormalHit;
            }
            return procEx;
        }

        public void SetAuraStack(uint spellId, Unit target, uint stack)
        {
            Aura aura = target.GetAura(spellId, GetGUID());
            if (aura == null)
                aura = AddAura(spellId, target);
            if (aura != null && stack != 0)
                aura.SetStackAmount((byte)stack);
        }

        public Spell FindCurrentSpellBySpellId(uint spell_id)
        {
            foreach (var spell in m_currentSpells.Values)
            {
                if (spell == null)
                    continue;
                if (spell.m_spellInfo.Id == spell_id)
                    return spell;
            }
            return null;
        }

        public int GetCurrentSpellCastTime(uint spell_id)
        {
            Spell spell = FindCurrentSpellBySpellId(spell_id);
            if (spell != null)
                return spell.GetCastTime();
            return 0;
        }

        public bool CanMoveDuringChannel()
        {
            Spell spell = m_currentSpells.LookupByKey(CurrentSpellTypes.Channeled);
            if (spell)
                if (spell.getState() != SpellState.Finished)
                    return spell.GetSpellInfo().HasAttribute(SpellAttr5.CanChannelWhenMoving) && spell.IsChannelActive();

            return false;
        }

        bool HasBreakableByDamageAuraType(AuraType type, uint excludeAura)
        {
            var auras = GetAuraEffectsByType(type);
            foreach (var eff in auras)
                if ((excludeAura == 0 || excludeAura != eff.GetSpellInfo().Id) && //Avoid self interrupt of channeled Crowd Control spells like Seduction
                    eff.GetSpellInfo().AuraInterruptFlags.HasAnyFlag(SpellAuraInterruptFlags.TakeDamage))
                    return true;
            return false;
        }

        public bool HasBreakableByDamageCrowdControlAura(Unit excludeCasterChannel = null)
        {
            uint excludeAura = 0;
            Spell currentChanneledSpell = excludeCasterChannel?.GetCurrentSpell(CurrentSpellTypes.Channeled);
            if (currentChanneledSpell != null)
                excludeAura = currentChanneledSpell.GetSpellInfo().Id; //Avoid self interrupt of channeled Crowd Control spells like Seduction

            return (HasBreakableByDamageAuraType(AuraType.ModConfuse, excludeAura)
                    || HasBreakableByDamageAuraType(AuraType.ModFear, excludeAura)
                    || HasBreakableByDamageAuraType(AuraType.ModStun, excludeAura)
                    || HasBreakableByDamageAuraType(AuraType.ModRoot, excludeAura)
                    || HasBreakableByDamageAuraType(AuraType.ModRoot2, excludeAura)
                    || HasBreakableByDamageAuraType(AuraType.Transform, excludeAura));
        }

        public uint GetDiseasesByCaster(ObjectGuid casterGUID, bool remove = false)
        {
            AuraType[] diseaseAuraTypes =
            {
                AuraType.PeriodicDamage, // Frost Fever and Blood Plague
                AuraType.Linked,          // Crypt Fever and Ebon Plague
                AuraType.None
            };

            uint diseases = 0;
            foreach (var aura in diseaseAuraTypes)
            {
                if (aura == AuraType.None)
                    break;

                for (var i = 0; i < m_modAuras[aura].Count;)
                {
                    var eff = m_modAuras[aura][i];
                    // Get auras with disease dispel type by caster
                    if (eff.GetSpellInfo().Dispel == DispelType.Disease
                        && eff.GetCasterGUID() == casterGUID)
                    {
                        ++diseases;

                        if (remove)
                        {
                            RemoveAura(eff.GetId(), eff.GetCasterGUID());
                            i = 0;
                            continue;
                        }
                    }
                    i++;
                }
            }
            return diseases;
        }

        uint GetDoTsByCaster(ObjectGuid casterGUID)
        {
            AuraType[] diseaseAuraTypes =
            {
                AuraType.PeriodicDamage,
                AuraType.PeriodicDamagePercent,
                AuraType.None
            };

            uint dots = 0;
            foreach (var aura in diseaseAuraTypes)
            {
                if (aura == AuraType.None)
                    break;

                var auras = GetAuraEffectsByType(aura);
                foreach (var eff in auras)
                {
                    // Get auras by caster
                    if (eff.GetCasterGUID() == casterGUID)
                        ++dots;
                }
            }
            return dots;
        }

        public void SendEnergizeSpellLog(Unit victim, uint spellId, int amount, int overEnergize, PowerType powerType)
        {
            SpellEnergizeLog data = new SpellEnergizeLog();
            data.CasterGUID = GetGUID();
            data.TargetGUID = victim.GetGUID();
            data.SpellID = spellId;
            data.Type = powerType;
            data.Amount = amount;
            data.OverEnergize = overEnergize;
            data.LogData.Initialize(victim);

            SendCombatLogMessage(data);
        }

        public void EnergizeBySpell(Unit victim, uint spellId, int damage, PowerType powerType)
        {
            int gain = victim.ModifyPower(powerType, damage);
            int overEnergize = damage - gain;

            SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(spellId);
            victim.getHostileRefManager().threatAssist(this, damage * 0.5f, spellInfo);

            SendEnergizeSpellLog(victim, spellId, damage, overEnergize, powerType);
        }

        public void ApplySpellImmune(uint spellId, SpellImmunity op, object type, bool apply)
        {
            if (apply)
            {
                m_spellImmune[op].RemoveAll(p => p.spellType == Convert.ToUInt32(type));

                SpellImmune Immune = new SpellImmune();
                Immune.spellId = spellId;
                Immune.spellType = Convert.ToUInt32(type);
                m_spellImmune.Add(op, Immune);
            }
            else
            {
                foreach (var spell in m_spellImmune[op].ToList())
                {
                    if (spell.spellId == spellId && spell.spellType == Convert.ToUInt32(type))
                    {
                        m_spellImmune.Remove(op, spell);
                        break;
                    }
                }
            }
        }
        public virtual bool IsImmunedToSpell(SpellInfo spellInfo)
        {
            if (spellInfo == null)
                return false;

            // Single spell immunity.
            var idList = m_spellImmune.LookupByKey(SpellImmunity.Id);
            foreach (var immune in idList)
                if (immune.spellType == spellInfo.Id)
                    return true;

            if (spellInfo.HasAttribute(SpellAttr0.UnaffectedByInvulnerability))
                return false;

            if (spellInfo.Dispel != 0)
            {
                var dispelList = m_spellImmune.LookupByKey(SpellImmunity.Dispel);
                foreach (var immune in dispelList)
                    if (immune.spellType == (int)spellInfo.Dispel)
                        return true;
            }

            // Spells that don't have effectMechanics.
            if (spellInfo.Mechanic != 0)
            {
                var mechanicList = m_spellImmune.LookupByKey(SpellImmunity.Mechanic);
                foreach (var immune in mechanicList)
                    if (immune.spellType == (int)spellInfo.Mechanic)
                        return true;
            }

            bool immuneToAllEffects = true;
            foreach (SpellEffectInfo effect in spellInfo.GetEffectsForDifficulty(GetMap().GetDifficultyID()))
            {
                // State/effect immunities applied by aura expect full spell immunity
                // Ignore effects with mechanic, they are supposed to be checked separately
                if (effect == null || !effect.IsEffect())
                    continue;

                if (!IsImmunedToSpellEffect(spellInfo, effect.EffectIndex))
                {
                    immuneToAllEffects = false;
                    break;
                }
            }

            if (immuneToAllEffects) //Return immune only if the target is immune to all spell effects.
                return true;

            if (spellInfo.Id != 42292 && spellInfo.Id != 59752)
            {
                var schoolList = m_spellImmune.LookupByKey(SpellImmunity.School);
                foreach (var immune in schoolList)
                {
                    SpellInfo immuneSpellInfo = Global.SpellMgr.GetSpellInfo(immune.spellId);
                    if (Convert.ToBoolean(immune.spellType & (uint)spellInfo.GetSchoolMask())
                        && !(immuneSpellInfo != null && immuneSpellInfo.IsPositive() && spellInfo.IsPositive())
                        && !spellInfo.CanPierceImmuneAura(immuneSpellInfo))
                        return true;
                }
            }

            return false;
        }
        public uint GetSchoolImmunityMask()
        {
            uint mask = 0;
            var mechanicList = m_spellImmune.LookupByKey(SpellImmunity.School);
            foreach (var spell in mechanicList)
                mask |= spell.spellType;

            return mask;
        }
        public uint GetMechanicImmunityMask()
        {
            uint mask = 0;
            var mechanicList = m_spellImmune.LookupByKey(SpellImmunity.Mechanic);
            foreach (var spell in mechanicList)
                mask |= (uint)(1 << (int)spell.spellType);

            return mask;
        }
        public virtual bool IsImmunedToSpellEffect(SpellInfo spellInfo, uint index)
        {
            if (spellInfo == null)
                return false;

            SpellEffectInfo effect = spellInfo.GetEffect(GetMap().GetDifficultyID(), index);
            if (effect == null || !effect.IsEffect())
                return false;

            // If m_immuneToEffect type contain this effect type, IMMUNE effect.
            uint eff = (uint)effect.Effect;
            var effectList = m_spellImmune.LookupByKey(SpellImmunity.Effect);
            foreach (var immune in effectList)
                if (immune.spellType == eff)
                    return true;

            uint mechanic = (uint)effect.Mechanic;
            if (mechanic != 0)
            {
                var mechanicList = m_spellImmune.LookupByKey(SpellImmunity.Mechanic);
                foreach (var immune in mechanicList)
                    if (immune.spellType == mechanic)
                        return true;
            }

            uint aura = (uint)effect.ApplyAuraName;
            if (aura != 0)
            {
                var list = m_spellImmune.LookupByKey(SpellImmunity.State);
                foreach (var immune in list)
                    if (immune.spellType == aura)
                        if (!spellInfo.HasAttribute(SpellAttr3.IgnoreHitResult))
                            return true;

                // Check for immune to application of harmful magical effects
                var immuneAuraApply = GetAuraEffectsByType(AuraType.ModImmuneAuraApplySchool);
                foreach (var immune in immuneAuraApply)
                    if (spellInfo.Dispel == DispelType.Magic &&                                      // Magic debuff
                        Convert.ToBoolean(immune.GetMiscValue() & (uint)spellInfo.GetSchoolMask()) &&  // Check school
                        !spellInfo.IsPositiveEffect(index))                                  // Harmful
                        return true;
            }

            return false;
        }
        public bool IsImmunedToDamage(SpellSchoolMask shoolMask)
        {
            // If m_immuneToSchool type contain this school type, IMMUNE damage.
            var schoolList = m_spellImmune.LookupByKey(SpellImmunity.School);
            foreach (var immune in schoolList)
                if (Convert.ToBoolean(immune.spellType & (uint)shoolMask))
                    return true;

            // If m_immuneToDamage type contain magic, IMMUNE damage.
            var damageList = m_spellImmune.LookupByKey(SpellImmunity.Damage);
            foreach (var immune in damageList)
                if (Convert.ToBoolean(immune.spellType & (uint)shoolMask))
                    return true;

            return false;
        }
        public bool IsImmunedToDamage(SpellInfo spellInfo)
        {
            if (spellInfo.HasAttribute(SpellAttr0.UnaffectedByInvulnerability))
                return false;

            uint shoolMask = (uint)spellInfo.GetSchoolMask();
            if (spellInfo.Id != 42292 && spellInfo.Id != 59752)
            {
                // If m_immuneToSchool type contain this school type, IMMUNE damage.
                var schoolList = m_spellImmune.LookupByKey(SpellImmunity.School);
                foreach (var immune in schoolList)
                    if (Convert.ToBoolean(immune.spellType & shoolMask) && !spellInfo.CanPierceImmuneAura(Global.SpellMgr.GetSpellInfo(immune.spellId)))
                        return true;
            }

            // If m_immuneToDamage type contain magic, IMMUNE damage.
            var damageList = m_spellImmune.LookupByKey(SpellImmunity.Damage);
            foreach (var immune in damageList)
                if (Convert.ToBoolean(immune.spellType & shoolMask))
                    return true;

            return false;
        }

        public void ProcDamageAndSpell(Unit victim, ProcFlags procAttacker, ProcFlags procVictim, ProcFlagsExLegacy procExtra, uint amount, WeaponAttackType attType = WeaponAttackType.BaseAttack, SpellInfo procSpell = null, SpellInfo procAura = null)
        {
            // Not much to do if no flags are set.
            if (procAttacker != 0)
                ProcDamageAndSpellFor(false, victim, procAttacker, procExtra, attType, procSpell, amount, procAura);
            // Now go on with a victim's events'n'auras
            // Not much to do if no flags are set or there is no victim
            if (victim != null && victim.IsAlive() && procVictim != 0)
                victim.ProcDamageAndSpellFor(true, this, procVictim, procExtra, attType, procSpell, amount, procAura);
        }
        void ProcDamageAndSpellFor(bool isVictim, Unit target, ProcFlags procFlag, ProcFlagsExLegacy procExtra, WeaponAttackType attType, SpellInfo procSpell, uint damage, SpellInfo procAura = null)
        {
            // Player is loaded now - do not allow passive spell casts to proc
            if (IsTypeId(TypeId.Player) && ToPlayer().GetSession().PlayerLoading())
                return;
            // For melee/ranged based attack need update skills and set some Aura states if victim present
            if (Convert.ToBoolean(procFlag & ProcFlags.MeleeBasedTriggerMask) && target != null)
            {
                // If exist crit/parry/dodge/block need update aura state (for victim and attacker)
                if (Convert.ToBoolean(procExtra & (ProcFlagsExLegacy.CriticalHit | ProcFlagsExLegacy.Parry | ProcFlagsExLegacy.Dodge | ProcFlagsExLegacy.Block)))
                {
                    // for victim
                    if (isVictim)
                    {
                        // if victim and dodge attack
                        if (Convert.ToBoolean(procExtra & ProcFlagsExLegacy.Dodge))
                        {
                            // Update AURA_STATE on dodge
                            if (GetClass() != Class.Rogue) // skip Rogue Riposte
                            {
                                ModifyAuraState(AuraStateType.Defense, true);
                                StartReactiveTimer(ReactiveType.Defense);
                            }
                        }
                        // if victim and parry attack
                        if (Convert.ToBoolean(procExtra & ProcFlagsExLegacy.Parry))
                        {
                            // For Hunters only Counterattack
                            if (GetClass() == Class.Hunter)
                            {
                                ModifyAuraState(AuraStateType.HunterParry, true);
                                StartReactiveTimer(ReactiveType.HunterParry);
                            }
                            else
                            {
                                ModifyAuraState(AuraStateType.Defense, true);
                                StartReactiveTimer(ReactiveType.Defense);
                            }
                        }
                        // if and victim block attack
                        if (Convert.ToBoolean(procExtra & ProcFlagsExLegacy.Block))
                        {
                            ModifyAuraState(AuraStateType.Defense, true);
                            StartReactiveTimer(ReactiveType.Defense);
                        }
                    }
                    else // For attacker
                    {
                        // Overpower on victim dodge
                        if (Convert.ToBoolean(procExtra & ProcFlagsExLegacy.Dodge) && IsTypeId(TypeId.Player) && GetClass() == Class.Warrior)
                        {
                            ToPlayer().AddComboPoints(1);
                            StartReactiveTimer(ReactiveType.OverPower);
                        }
                    }
                }
            }

            Unit actor = isVictim ? target : this;
            Unit actionTarget = !isVictim ? target : this;

            DamageInfo damageInfo = new DamageInfo(actor, actionTarget, damage, procSpell, (procSpell != null ? procSpell.SchoolMask : SpellSchoolMask.Normal), DamageEffectType.Direct, attType);
            HealInfo healInfo = new HealInfo(actor, actionTarget, damage, procSpell, (procSpell != null ? procSpell.SchoolMask : SpellSchoolMask.Normal));
            ProcEventInfo eventInfo = new ProcEventInfo(actor, actionTarget, target, (uint)procFlag, 0, 0, (uint)procExtra, null, damageInfo, healInfo);

            var now = DateTime.Now;
            List<ProcTriggeredData> procTriggered = new List<ProcTriggeredData>();
            // Fill procTriggered list
            foreach (var pair in GetAppliedAuras())
            {
                // Do not allow auras to proc from effect triggered by itself
                if (procAura != null && procAura.Id == pair.Key || pair.Value == null)
                    continue;

                if (pair.Value.GetBase().IsProcOnCooldown(now))
                    continue;

                ProcTriggeredData triggerData = new ProcTriggeredData(pair.Value.GetBase());
                // Defensive procs are active on absorbs (so absorption effects are not a hindrance)
                bool active = damage != 0 || (Convert.ToBoolean(procExtra & ProcFlagsExLegacy.Block) && isVictim);
                if (isVictim)
                    procExtra &= ~ProcFlagsExLegacy.InternalReqFamily;

                SpellInfo spellProto = pair.Value.GetBase().GetSpellInfo();

                // only auras that has triggered spell should proc from fully absorbed damage
                if (procExtra.HasAnyFlag(ProcFlagsExLegacy.Absorb) && isVictim && damage != 0)
                {
                    foreach (SpellEffectInfo effect in pair.Value.GetBase().GetSpellEffectInfos())
                    {
                        if (effect != null && effect.TriggerSpell != 0)
                        {
                            active = true;
                            break;
                        }
                    }
                }

                if (!IsTriggeredAtSpellProcEvent(target, triggerData.aura, procSpell, procFlag, procExtra, attType, isVictim, active, ref triggerData.spellProcEvent))
                    continue;

                // do checks using conditions table
                if (!Global.ConditionMgr.IsObjectMeetingNotGroupedConditions(ConditionSourceType.SpellProc, spellProto.Id, eventInfo.GetActor(), eventInfo.GetActionTarget()))
                    continue;

                // AuraScript Hook
                if (!triggerData.aura.CallScriptCheckProcHandlers(pair.Value, eventInfo))
                    continue;

                bool procSuccess = RollProcResult(target, triggerData.aura, attType, isVictim, triggerData.spellProcEvent);
                triggerData.aura.SetLastProcAttemptTime(now);
                if (!procSuccess)
                    continue;

                bool triggeredCanProcAura = true;
                // Additional checks for triggered spells (ignore trap casts)
                if (procExtra.HasAnyFlag(ProcFlagsExLegacy.InternalTriggered) && !procFlag.HasAnyFlag(ProcFlags.DoneTrapActivation))
                {
                    if (!spellProto.HasAttribute(SpellAttr3.CanProcWithTriggered))
                        triggeredCanProcAura = false;
                }

                foreach (AuraEffect aurEff in pair.Value.GetBase().GetAuraEffects())
                {
                    if (aurEff != null)
                    {
                        // Skip this auras
                        if (isNonTriggerAura(aurEff.GetAuraType()))
                            continue;
                        // If not trigger by default and spellProcEvent == null - skip
                        if (!isTriggerAura(aurEff.GetAuraType()) && triggerData.spellProcEvent == null)
                            continue;
                        // Some spells must always trigger
                        if (triggeredCanProcAura || isAlwaysTriggeredAura(aurEff.GetAuraType()))
                            triggerData.effMask |= (uint)(1 << aurEff.GetEffIndex());
                    }
                }
                if (triggerData.effMask != 0)
                    procTriggered.Add(triggerData);
            }

            // Nothing found
            if (procTriggered.Empty())
                return;

            // Note: must SetCantProc(false) before return
            if (procExtra.HasAnyFlag(ProcFlagsExLegacy.InternalTriggered | ProcFlagsExLegacy.InternalCantProc))
                SetCantProc(true);

            // Handle effects proceed this time
            foreach (var proc in procTriggered)
            {
                // look for aura in auras list, it may be removed while proc event processing
                if (proc.aura.IsRemoved())
                    continue;

                bool useCharges = proc.aura.IsUsingCharges();
                // no more charges to use, prevent proc
                if (useCharges && proc.aura.GetCharges() == 0)
                    continue;

                bool takeCharges = false;
                SpellInfo spellInfo = proc.aura.GetSpellInfo();
                uint Id = proc.aura.GetId();

                AuraApplication aurApp = proc.aura.GetApplicationOfTarget(GetGUID());

                bool prepare = proc.aura.CallScriptPrepareProcHandlers(aurApp, eventInfo);

                TimeSpan cooldown = TimeSpan.Zero;
                if (prepare)
                {
                    cooldown = TimeSpan.FromMilliseconds(spellInfo.ProcCooldown);
                    if (proc.spellProcEvent != null && proc.spellProcEvent.cooldown != 0)
                        cooldown = TimeSpan.FromSeconds(proc.spellProcEvent.cooldown);
                }

                proc.aura.SetLastProcSuccessTime(now);

                // Note: must SetCantProc(false) before return
                if (spellInfo.HasAttribute(SpellAttr3.DisableProc))
                    SetCantProc(true);

                bool handled = proc.aura.CallScriptProcHandlers(aurApp, eventInfo);

                // "handled" is needed as long as proc can be handled in multiple places
                if (!handled)
                {
                    Log.outDebug(LogFilter.Spells, "ProcDamageAndSpell: casting spell {0} (triggered with value by {1} aura of spell {2})", spellInfo.Id, (isVictim ? "a victim's" : "an attacker's"), Id);
                    takeCharges = true;
                }

                if (!handled)
                {
                    for (byte effIndex = 0; effIndex < SpellConst.MaxEffects; ++effIndex)
                    {
                        if (!Convert.ToBoolean(proc.effMask & (1 << effIndex)))
                            continue;

                        AuraEffect triggeredByAura = proc.aura.GetEffect(effIndex);
                        Contract.Assert(triggeredByAura != null);

                        bool prevented = proc.aura.CallScriptEffectProcHandlers(triggeredByAura, aurApp, eventInfo);
                        if (prevented)
                        {
                            takeCharges = true;
                            continue;
                        }

                        switch (triggeredByAura.GetAuraType())
                        {
                            case AuraType.ProcTriggerSpell:
                                {
                                    Log.outDebug(LogFilter.Spells, "ProcDamageAndSpell: casting spell {0} (triggered by {1} aura of spell {2})", spellInfo.Id, (isVictim ? "a victim's" : "an attacker's"), triggeredByAura.GetId());
                                    // Don`t drop charge or add cooldown for not started trigger
                                    if (HandleProcTriggerSpell(target, damage, triggeredByAura, procSpell, procFlag, procExtra))
                                        takeCharges = true;
                                    break;
                                }
                            case AuraType.ProcTriggerDamage:
                                {
                                    // target has to be valid
                                    if (eventInfo.GetProcTarget() == null)
                                        break;

                                    triggeredByAura.HandleProcTriggerDamageAuraProc(aurApp, eventInfo); // this function is part of the new proc system
                                    takeCharges = true;
                                    break;
                                }
                            case AuraType.ManaShield:
                            case AuraType.Dummy:
                                {
                                    Log.outDebug(LogFilter.Spells, "ProcDamageAndSpell: casting spell id {0} (triggered by {1} dummy aura of spell {2})", spellInfo.Id, (isVictim ? "a victim's" : "an attacker's"), triggeredByAura.GetId());
                                    if (HandleDummyAuraProc(target, damage, triggeredByAura, procSpell, procFlag, procExtra))
                                        takeCharges = true;
                                    break;
                                }
                            case AuraType.ProcOnPowerAmount2:
                            case AuraType.ProcOnPowerAmount:
                                {
                                    triggeredByAura.HandleProcTriggerSpellOnPowerAmountAuraProc(aurApp, eventInfo);
                                    takeCharges = true;
                                    break;
                                }
                            case AuraType.ObsModPower:
                            case AuraType.ModSpellCritChance:
                            case AuraType.ModDamagePercentTaken:
                            case AuraType.ModMeleeHaste:
                            case AuraType.ModMeleeHaste3:
                                Log.outDebug(LogFilter.Spells, "ProcDamageAndSpell: casting spell id {0} (triggered by {1} aura of spell {2})", spellInfo.Id, isVictim ? "a victim's" : "an attacker's", triggeredByAura.GetId());
                                takeCharges = true;
                                break;
                            case AuraType.OverrideClassScripts:
                                {
                                    Log.outDebug(LogFilter.Spells, "ProcDamageAndSpell: casting spell id {0} (triggered by {1} aura of spell {2})", spellInfo.Id, (isVictim ? "a victim's" : "an attacker's"), triggeredByAura.GetId());
                                    if (HandleOverrideClassScriptAuraProc(target, damage, triggeredByAura, procSpell))
                                        takeCharges = true;
                                    break;
                                }
                            case AuraType.RaidProcFromChargeWithValue:
                                {
                                    Log.outDebug(LogFilter.Spells, "ProcDamageAndSpell: casting mending (triggered by {0} dummy aura of spell {1})",
                                        (isVictim ? "a victim's" : "an attacker's"), triggeredByAura.GetId());

                                    HandleAuraRaidProcFromChargeWithValue(triggeredByAura);
                                    takeCharges = true;
                                    break;
                                }
                            case AuraType.RaidProcFromCharge:
                                {
                                    Log.outDebug(LogFilter.Spells, "ProcDamageAndSpell: casting mending (triggered by {0} dummy aura of spell {1})",
                                        (isVictim ? "a victim's" : "an attacker's"), triggeredByAura.GetId());

                                    HandleAuraRaidProcFromCharge(triggeredByAura);
                                    takeCharges = true;
                                    break;
                                }
                            case AuraType.ProcTriggerSpellWithValue:
                                {
                                    Log.outDebug(LogFilter.Spells, "ProcDamageAndSpell: casting spell {0} (triggered with value by {1} aura of spell {2})", spellInfo.Id, (isVictim ? "a victim's" : "an attacker's"), triggeredByAura.GetId());

                                    if (HandleProcTriggerSpell(target, damage, triggeredByAura, procSpell, procFlag, procExtra))
                                        takeCharges = true;
                                    break;
                                }
                            case AuraType.ModCastingSpeedNotStack:
                                // Skip melee hits or instant cast spells
                                if (procSpell != null && procSpell.CalcCastTime() != 0)
                                    takeCharges = true;
                                break;
                            case AuraType.ReflectSpellsSchool:
                                // Skip Melee hits and spells ws wrong school
                                if (procSpell != null && Convert.ToBoolean(triggeredByAura.GetMiscValue() & (int)procSpell.SchoolMask))         // School check
                                    takeCharges = true;
                                break;
                            case AuraType.SpellMagnet:
                                // Skip Melee hits and targets with magnet aura
                                if (procSpell != null && (triggeredByAura.GetBase().GetUnitOwner().ToUnit() == ToUnit()))         // Magnet
                                    takeCharges = true;
                                break;
                            case AuraType.ModPowerCostSchoolPct:
                            case AuraType.ModPowerCostSchool:
                                // Skip melee hits and spells ws wrong school or zero cost
                                if (procSpell != null && Convert.ToBoolean(triggeredByAura.GetMiscValue() & (int)procSpell.SchoolMask)) // School check
                                {
                                    var costs = procSpell.CalcPowerCost(this, procSpell.GetSchoolMask());
                                    var m = costs.Find(cost => cost.Amount > 0);
                                    if (m != null)
                                        takeCharges = true;
                                }
                                break;
                            case AuraType.MechanicImmunity:
                                // Compare mechanic
                                if (procSpell != null && procSpell.Mechanic == (Mechanics)triggeredByAura.GetMiscValue())
                                    takeCharges = true;
                                break;
                            case AuraType.ModMechanicResistance:
                                // Compare mechanic
                                if (procSpell != null && procSpell.Mechanic == (Mechanics)triggeredByAura.GetMiscValue())
                                    takeCharges = true;
                                break;
                            case AuraType.ModSpellDamageFromCaster:
                                // Compare casters
                                if (triggeredByAura.GetCasterGUID() == target.GetGUID())
                                    takeCharges = true;
                                break;
                            // CC Auras which use their amount amount to drop
                            // Are there any more auras which need this?
                            case AuraType.ModConfuse:
                            case AuraType.ModFear:
                            case AuraType.ModStun:
                            case AuraType.ModRoot:
                            case AuraType.Transform:
                            case AuraType.ModRoot2:
                                {
                                    // chargeable mods are breaking on hit
                                    if (useCharges)
                                        takeCharges = true;
                                    else
                                    {
                                        // Spell own direct damage at apply wont break the CC
                                        if (procSpell != null && (procSpell.Id == triggeredByAura.GetId()))
                                        {
                                            Aura aura = triggeredByAura.GetBase();
                                            // called from spellcast, should not have ticked yet
                                            if (aura.GetDuration() == aura.GetMaxDuration())
                                                break;
                                        }
                                        int damageLeft = triggeredByAura.GetAmount();
                                        // No damage left
                                        if (damageLeft < damage)
                                            proc.aura.Remove();
                                        else
                                            triggeredByAura.SetAmount((int)(damageLeft - damage));
                                    }
                                    break;
                                }
                            default:
                                // nothing do, just charges counter
                                takeCharges = true;
                                break;
                        }
                        proc.aura.CallScriptAfterEffectProcHandlers(triggeredByAura, aurApp, eventInfo);
                    }
                }

                if (prepare && takeCharges && cooldown != TimeSpan.Zero)
                    proc.aura.AddProcCooldown(now + cooldown);

                // Remove charge (aura can be removed by triggers)
                if (prepare && useCharges && takeCharges)
                {
                    // Set charge drop delay (only for missiles)
                    if (procExtra.HasAnyFlag(ProcFlagsExLegacy.Reflect) && target && procSpell != null && procSpell.Speed > 0.0f)
                    {
                        // Set up missile speed based delay (from Spell.cpp: Spell.AddUnitTarget().L2237)
                        uint delay = (uint)Math.Floor(Math.Max(target.GetDistance(this), 5.0f) / procSpell.Speed * 1000.0f);
                        // Schedule charge drop
                        proc.aura.DropChargeDelayed(delay);
                    }
                    else
                        proc.aura.DropCharge();
                }

                proc.aura.CallScriptAfterProcHandlers(aurApp, eventInfo);

                if (spellInfo.HasAttribute(SpellAttr3.DisableProc))
                    SetCantProc(false);
            }

            // Cleanup proc requirements
            if (procExtra.HasAnyFlag(ProcFlagsExLegacy.InternalTriggered | ProcFlagsExLegacy.InternalCantProc))
                SetCantProc(false);
        }

        void SetCantProc(bool apply)
        {
            if (apply)
                ++m_procDeep;
            else
            {
                Contract.Assert(m_procDeep != 0);
                --m_procDeep;
            }
        }

        public void CastStop(uint except_spellid = 0)
        {
            for (var i = CurrentSpellTypes.Generic; i < CurrentSpellTypes.Max; i++)
                if (GetCurrentSpell(i) != null && GetCurrentSpell(i).m_spellInfo.Id != except_spellid)
                    InterruptSpell(i, false);
        }
        public void ModSpellCastTime(SpellInfo spellInfo, ref int castTime, Spell spell = null)
        {
            if (spellInfo == null || castTime < 0)
                return;

            // called from caster
            Player modOwner = GetSpellModOwner();
            if (modOwner != null)
                modOwner.ApplySpellMod(spellInfo.Id, SpellModOp.CastingTime, ref castTime, spell);

            if (!(spellInfo.HasAttribute(SpellAttr0.Ability | SpellAttr0.Tradespell) || spellInfo.HasAttribute(SpellAttr3.NoDoneBonus))
                && (IsTypeId(TypeId.Player) && spellInfo.SpellFamilyName != 0) || IsTypeId(TypeId.Unit))
                castTime = (int)(castTime * GetFloatValue(UnitFields.ModCastSpeed));
            else if (spellInfo.HasAttribute(SpellAttr0.ReqAmmo) && !spellInfo.HasAttribute(SpellAttr2.AutorepeatFlag))
                castTime = (int)(castTime * m_modAttackSpeedPct[(int)WeaponAttackType.RangedAttack]);
            else if (Global.SpellMgr.IsPartOfSkillLine(SkillType.Cooking, spellInfo.Id) && HasAura(67556)) // cooking with Chef Hat.
                castTime = 500;
        }
        public void ModSpellDurationTime(SpellInfo spellInfo, ref int duration, Spell spell = null)
        {
            if (spellInfo == null || duration < 0)
                return;

            if (spellInfo.IsChanneled() && !spellInfo.HasAttribute(SpellAttr5.HasteAffectDuration))
                return;

            // called from caster
            Player modOwner = GetSpellModOwner();
            if (modOwner)
                modOwner.ApplySpellMod(spellInfo.Id, SpellModOp.CastingTime, ref duration, spell);

            if (!(spellInfo.HasAttribute(SpellAttr0.Ability) || spellInfo.HasAttribute(SpellAttr0.Tradespell) || spellInfo.HasAttribute(SpellAttr3.NoDoneBonus)) &&
                (IsTypeId(TypeId.Player) && spellInfo.SpellFamilyName != 0) || IsTypeId(TypeId.Unit))
                duration = (int)(duration * GetFloatValue(UnitFields.ModCastSpeed));
            else if (spellInfo.HasAttribute(SpellAttr0.ReqAmmo) && !spellInfo.HasAttribute(SpellAttr2.AutorepeatFlag))
                duration = (int)(duration * m_modAttackSpeedPct[(int)WeaponAttackType.RangedAttack]);
        }
        public float ApplyEffectModifiers(SpellInfo spellProto, uint effect_index, float value)
        {
            Player modOwner = GetSpellModOwner();
            if (modOwner != null)
            {
                modOwner.ApplySpellMod(spellProto.Id, SpellModOp.AllEffects, ref value);
                switch (effect_index)
                {
                    case 0:
                        modOwner.ApplySpellMod(spellProto.Id, SpellModOp.Effect1, ref value);
                        break;
                    case 1:
                        modOwner.ApplySpellMod(spellProto.Id, SpellModOp.Effect2, ref value);
                        break;
                    case 2:
                        modOwner.ApplySpellMod(spellProto.Id, SpellModOp.Effect3, ref value);
                        break;
                    case 3:
                        modOwner.ApplySpellMod(spellProto.Id, SpellModOp.Effect4, ref value);
                        break;
                    case 4:
                        modOwner.ApplySpellMod(spellProto.Id, SpellModOp.Effect5, ref value);
                        break;
                }
            }
            return value;
        }

        public ushort GetMaxSkillValueForLevel(Unit target = null)
        {
            return (ushort)(target != null ? GetLevelForTarget(target) : getLevel() * 5);
        }
        public Player GetSpellModOwner()
        {
            if (IsTypeId(TypeId.Player))
                return ToPlayer();
            if (IsPet() || IsTotem())
            {
                Unit owner = GetOwner();
                if (owner != null && owner.IsTypeId(TypeId.Player))
                    return owner.ToPlayer();
            }
            return null;
        }

        public Spell GetCurrentSpell(CurrentSpellTypes spellType)
        {
            return m_currentSpells.LookupByKey(spellType);
        }
        public void SetCurrentCastSpell(Spell pSpell)
        {
            Contract.Assert(pSpell != null);                                         // NULL may be never passed here, use InterruptSpell or InterruptNonMeleeSpells

            CurrentSpellTypes CSpellType = pSpell.GetCurrentContainer();

            if (pSpell == GetCurrentSpell(CSpellType))             // avoid breaking self
                return;

            // break same type spell if it is not delayed
            InterruptSpell(CSpellType, false);

            // special breakage effects:
            switch (CSpellType)
            {
                case CurrentSpellTypes.Generic:
                    {
                        // generic spells always break channeled not delayed spells
                        InterruptSpell(CurrentSpellTypes.Channeled, false);

                        // autorepeat breaking
                        if (GetCurrentSpell(CurrentSpellTypes.AutoRepeat) != null)
                        {
                            // break autorepeat if not Auto Shot
                            if (m_currentSpells[CurrentSpellTypes.AutoRepeat].m_spellInfo.Id != 75)
                                InterruptSpell(CurrentSpellTypes.AutoRepeat);
                            m_AutoRepeatFirstCast = true;
                        }
                        if (pSpell.m_spellInfo.CalcCastTime(getLevel()) > 0)
                            AddUnitState(UnitState.Casting);

                        break;
                    }
                case CurrentSpellTypes.Channeled:
                    {
                        // channel spells always break generic non-delayed and any channeled spells
                        InterruptSpell(CurrentSpellTypes.Generic, false);
                        InterruptSpell(CurrentSpellTypes.Channeled);

                        // it also does break autorepeat if not Auto Shot
                        if (GetCurrentSpell(CurrentSpellTypes.AutoRepeat) != null &&
                            m_currentSpells[CurrentSpellTypes.AutoRepeat].m_spellInfo.Id != 75)
                            InterruptSpell(CurrentSpellTypes.AutoRepeat);
                        AddUnitState(UnitState.Casting);

                        break;
                    }
                case CurrentSpellTypes.AutoRepeat:
                    {
                        // only Auto Shoot does not break anything
                        if (pSpell.m_spellInfo.Id != 75)
                        {
                            // generic autorepeats break generic non-delayed and channeled non-delayed spells
                            InterruptSpell(CurrentSpellTypes.Generic, false);
                            InterruptSpell(CurrentSpellTypes.Channeled, false);
                        }
                        // special action: set first cast flag
                        m_AutoRepeatFirstCast = true;

                        break;
                    }
                default:
                    break; // other spell types don't break anything now
            }

            // current spell (if it is still here) may be safely deleted now
            if (GetCurrentSpell(CSpellType) != null)
                m_currentSpells[CSpellType].SetReferencedFromCurrent(false);

            // set new current spell
            m_currentSpells[CSpellType] = pSpell;
            pSpell.SetReferencedFromCurrent(true);

            pSpell.m_selfContainer = m_currentSpells[pSpell.GetCurrentContainer()];
        }

        public bool IsNonMeleeSpellCast(bool withDelayed, bool skipChanneled = false, bool skipAutorepeat = false, bool isAutoshoot = false, bool skipInstant = true)
        {
            // We don't do loop here to explicitly show that melee spell is excluded.
            // Maybe later some special spells will be excluded too.

            // generic spells are cast when they are not finished and not delayed
            var currentSpell = GetCurrentSpell(CurrentSpellTypes.Generic);
            if (currentSpell &&
                    (currentSpell.getState() != SpellState.Finished) &&
                    (withDelayed || currentSpell.getState() != SpellState.Delayed))
            {
                if (!skipInstant || currentSpell.GetCastTime() != 0)
                {
                    if (!isAutoshoot || !currentSpell.m_spellInfo.HasAttribute(SpellAttr2.NotResetAutoActions))
                        return true;
                }
            }
            currentSpell = GetCurrentSpell(CurrentSpellTypes.Channeled);
            // channeled spells may be delayed, but they are still considered cast
            if (!skipChanneled && currentSpell &&
                (currentSpell.getState() != SpellState.Finished))
            {
                if (!isAutoshoot || !currentSpell.m_spellInfo.HasAttribute(SpellAttr2.NotResetAutoActions))
                    return true;
            }
            currentSpell = GetCurrentSpell(CurrentSpellTypes.AutoRepeat);
            // autorepeat spells may be finished or delayed, but they are still considered cast
            if (!skipAutorepeat && currentSpell)
                return true;

            return false;
        }

        public uint SpellCriticalDamageBonus(SpellInfo spellProto, uint damage, Unit victim = null)
        {
            // Calculate critical bonus
            int crit_bonus = (int)damage;
            float crit_mod = 0.0f;

            switch (spellProto.DmgClass)
            {
                case SpellDmgClass.Melee:                      // for melee based spells is 100%
                case SpellDmgClass.Ranged:
                    // @todo write here full calculation for melee/ranged spells
                    crit_bonus += (int)damage;
                    break;
                default:
                    crit_bonus += (int)damage / 2;                       // for spells is 50%
                    break;
            }

            crit_mod += (GetTotalAuraMultiplierByMiscMask(AuraType.ModCritDamageBonus, (uint)spellProto.GetSchoolMask()) - 1.0f) * 100;

            if (crit_bonus != 0)
                MathFunctions.AddPct(ref crit_bonus, (int)crit_mod);

            crit_bonus -= (int)damage;

            if (damage > crit_bonus)
            {
                // adds additional damage to critBonus (from talents)
                Player modOwner = GetSpellModOwner();
                if (modOwner != null)
                    modOwner.ApplySpellMod(spellProto.Id, SpellModOp.CritDamageBonus, ref crit_bonus);
            }

            crit_bonus += (int)damage;

            return (uint)crit_bonus;
        }

        bool isSpellBlocked(Unit victim, SpellInfo spellProto, WeaponAttackType attackType = WeaponAttackType.BaseAttack)
        {
            // These spells can't be blocked
            if (spellProto != null && spellProto.HasAttribute(SpellAttr0.ImpossibleDodgeParryBlock))
                return false;

            // Can't block when casting/controlled
            if (victim.IsNonMeleeSpellCast(false) || victim.HasUnitState(UnitState.Controlled))
                return false;

            if (victim.HasAuraType(AuraType.IgnoreHitDirection) || victim.HasInArc(MathFunctions.PI, this))
            {
                // Check creatures flags_extra for disable block
                if (victim.IsTypeId(TypeId.Unit) &&
                    victim.ToCreature().GetCreatureTemplate().FlagsExtra.HasAnyFlag(CreatureFlagsExtra.NoBlock))
                    return false;

                float blockChance = GetUnitBlockChance(attackType, victim);
                if (RandomHelper.randChance(blockChance))
                    return true;
            }
            return false;
        }

        public void _DeleteRemovedAuras()
        {
            while (!m_removedAuras.Empty())
            {
                m_removedAuras.Remove(m_removedAuras.First());
            }
        }

        bool IsTriggeredAtSpellProcEvent(Unit victim, Aura aura, SpellInfo procSpell, ProcFlags procFlag, ProcFlagsExLegacy procExtra, WeaponAttackType attType, bool isVictim, bool active, ref SpellProcEventEntry spellProcEvent)
        {
            SpellInfo spellInfo = aura.GetSpellInfo();

            // let the aura be handled by new proc system if it has new entry
            if (Global.SpellMgr.GetSpellProcEntry(spellInfo.Id) != null)
                return false;

            // Get proc Event Entry
            spellProcEvent = Global.SpellMgr.GetSpellProcEvent(spellInfo.Id);

            // Get EventProcFlag
            ProcFlags EventProcFlag;
            if (spellProcEvent != null && spellProcEvent.procFlags != 0) // if exist get custom spellProcEvent.procFlags
                EventProcFlag = (ProcFlags)spellProcEvent.procFlags;
            else
                EventProcFlag = spellInfo.ProcFlags;       // else get from spell proto
            // Continue if no trigger exist
            if (EventProcFlag == 0)
                return false;

            // Check spellProcEvent data requirements
            if (!Global.SpellMgr.IsSpellProcEventCanTriggeredBy(spellInfo, spellProcEvent, EventProcFlag, procSpell, procFlag, procExtra, active))
                return false;
            // In most cases req get honor or XP from kill
            if (EventProcFlag.HasAnyFlag(ProcFlags.Kill) && IsTypeId(TypeId.Player))
            {
                bool allow = false;

                if (victim != null)
                    allow = ToPlayer().isHonorOrXPTarget(victim);

                // Shadow Word: Death - can trigger from every kill
                if (aura.GetId() == 32409)
                    allow = true;
                if (!allow)
                    return false;
            }
            // Aura added by spell can`t trigger from self (prevent drop charges/do triggers)
            // But except periodic and kill triggers (can triggered from self)
            if (procSpell != null && procSpell.Id == spellInfo.Id
                && !spellInfo.ProcFlags.HasAnyFlag(ProcFlags.TakenPeriodic | ProcFlags.Kill))
                return false;

            // Check if current equipment allows aura to proc
            if (!isVictim && IsTypeId(TypeId.Player))
            {
                Player player = ToPlayer();
                if (spellInfo.EquippedItemClass == ItemClass.Weapon)
                {
                    Item item = null;
                    if (attType == WeaponAttackType.BaseAttack || attType == WeaponAttackType.RangedAttack)
                        item = player.GetUseableItemByPos(InventorySlots.Bag0, EquipmentSlot.MainHand);
                    else if (attType == WeaponAttackType.OffAttack)
                        item = player.GetUseableItemByPos(InventorySlots.Bag0, EquipmentSlot.OffHand);

                    if (player.IsInFeralForm())
                        return false;

                    if (item == null || item.IsBroken() || item.GetTemplate().GetClass() != ItemClass.Weapon || !Convert.ToBoolean((1 << (int)item.GetTemplate().GetSubClass()) & spellInfo.EquippedItemSubClassMask))
                        return false;
                }
                else if (spellInfo.EquippedItemClass == ItemClass.Armor)
                {
                    // Check if player is wearing shield
                    Item item = player.GetUseableItemByPos(InventorySlots.Bag0, EquipmentSlot.OffHand);
                    if (item == null || item.IsBroken() || item.GetTemplate().GetClass() != ItemClass.Armor || !Convert.ToBoolean((1 << (int)item.GetTemplate().GetSubClass()) & spellInfo.EquippedItemSubClassMask))
                        return false;
                }
            }

            return true;
        }

        bool RollProcResult(Unit victim, Aura aura, WeaponAttackType attType, bool isVictim, SpellProcEventEntry spellProcEvent)
        {
            SpellInfo spellInfo = aura.GetSpellInfo();
            // Get chance from spell
            float chance = spellInfo.ProcChance;
            // If in spellProcEvent exist custom chance, chance = spellProcEvent.customChance;
            if (spellProcEvent != null && spellProcEvent.customChance != 0.0f)
                chance = spellProcEvent.customChance;
            // If PPM exist calculate chance from PPM
            if (spellProcEvent != null && spellProcEvent.ppmRate != 0)
            {
                if (!isVictim)
                {
                    uint WeaponSpeed = GetBaseAttackTime(attType);
                    chance = GetPPMProcChance(WeaponSpeed, spellProcEvent.ppmRate, spellInfo);
                }
                else
                {
                    uint WeaponSpeed = victim.GetBaseAttackTime(attType);
                    chance = victim.GetPPMProcChance(WeaponSpeed, spellProcEvent.ppmRate, spellInfo);
                }
            }

            if (spellInfo.ProcBasePPM > 0.0f)
                chance = aura.CalcPPMProcChance(isVictim ? victim : this);

            // Apply chance modifer aura
            Player modOwner = GetSpellModOwner();
            if (modOwner != null)
                modOwner.ApplySpellMod(spellInfo.Id, SpellModOp.ChanceOfSuccess, ref chance);

            return RandomHelper.randChance(chance);
        }

        uint getTransForm() { return m_transform; }

        public bool HasStealthAura() { return HasAuraType(AuraType.ModStealth); }
        public bool HasInvisibilityAura() { return HasAuraType(AuraType.ModInvisibility); }
        public bool isFeared() { return HasAuraType(AuraType.ModFear); }
        public bool isFrozen() { return HasAuraState(AuraStateType.Frozen); }
        public bool IsPolymorphed()
        {
            uint transformId = getTransForm();
            if (transformId == 0)
                return false;

            SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(transformId);
            if (spellInfo == null)
                return false;

            return spellInfo.GetSpellSpecific() == SpellSpecificType.MagePolymorph;
        }

        public int HealBySpell(Unit victim, SpellInfo spellInfo, uint addHealth, bool critical = false)
        {
            uint absorb = 0;
            // calculate heal absorb and reduce healing
            CalcHealAbsorb(victim, spellInfo, ref addHealth, ref absorb);

            int gain = DealHeal(victim, addHealth);
            SendHealSpellLog(victim, spellInfo.Id, addHealth, (uint)(addHealth - gain), absorb, critical);
            return gain;
        }
        public int DealHeal(Unit victim, uint addhealth)
        {
            int gain = 0;

            if (victim.IsAIEnabled)
                victim.GetAI().HealReceived(this, addhealth);

            if (IsAIEnabled)
                GetAI().HealDone(victim, addhealth);

            if (addhealth != 0)
                gain = (int)victim.ModifyHealth(addhealth);

            // Hook for OnHeal Event
            uint tempGain = (uint)gain;
            Global.ScriptMgr.OnHeal(this, victim, ref tempGain);
            gain = (int)tempGain;

            Unit unit = this;

            if (IsTypeId(TypeId.Unit) && IsTotem())
                unit = GetOwner();

            Player player = unit.ToPlayer();
            if (player != null)
            {
                Battleground bg = player.GetBattleground();
                if (bg)
                    bg.UpdatePlayerScore(player, ScoreType.HealingDone, (uint)gain);

                // use the actual gain, as the overheal shall not be counted, skip gain 0 (it ignored anyway in to criteria)
                if (gain != 0)
                    player.UpdateCriteria(CriteriaTypes.HealingDone, (uint)gain, 0, 0, victim);

                player.UpdateCriteria(CriteriaTypes.HighestHealCasted, addhealth);
            }

            if ((player = victim.ToPlayer()) != null)
            {
                player.UpdateCriteria(CriteriaTypes.TotalHealingReceived, (uint)gain);
                player.UpdateCriteria(CriteriaTypes.HighestHealingReceived, addhealth);
            }

            return gain;
        }

        void SendHealSpellLog(Unit victim, uint spellID, uint health, uint overHeal, uint absorbed, bool crit)
        {
            SpellHealLog spellHealLog = new SpellHealLog();

            spellHealLog.TargetGUID = victim.GetGUID();
            spellHealLog.CasterGUID = GetGUID();

            spellHealLog.SpellID = spellID;
            spellHealLog.Health = health;
            spellHealLog.OverHeal = overHeal;
            spellHealLog.Absorbed = absorbed;

            spellHealLog.Crit = crit;

            /// @todo: 6.x Has to be implemented
            /*
            spellHealLog.WriteBit("Multistrike");

            var hasCritRollMade = spellHealLog.WriteBit("HasCritRollMade");
            var hasCritRollNeeded = spellHealLog.WriteBit("HasCritRollNeeded");
            var hasLogData = spellHealLog.WriteBit("HasLogData");

            if (hasCritRollMade)
                packet.ReadSingle("CritRollMade");

            if (hasCritRollNeeded)
                packet.ReadSingle("CritRollNeeded");

            if (hasLogData)
                SpellParsers.ReadSpellCastLogData(packet);
            */
            spellHealLog.LogData.Initialize(victim);
            SendCombatLogMessage(spellHealLog);
        }

        bool HandleDummyAuraProc(Unit victim, uint damage, AuraEffect triggeredByAura, SpellInfo procSpell, ProcFlags procFlag, ProcFlagsExLegacy procEx)
        {
            SpellInfo dummySpell = triggeredByAura.GetSpellInfo();
            int triggerAmount = triggeredByAura.GetAmount();

            Item castItem = !triggeredByAura.GetBase().GetCastItemGUID().IsEmpty() && IsTypeId(TypeId.Player)
                ? ToPlayer().GetItemByGuid(triggeredByAura.GetBase().GetCastItemGUID()) : null;

            Unit target = victim;
            uint triggered_spell_id = triggeredByAura.GetSpellEffectInfo().TriggerSpell;

            // processed charge only counting case
            if (triggered_spell_id == 0)
                return true;

            SpellInfo triggerEntry = Global.SpellMgr.GetSpellInfo(triggered_spell_id);
            if (triggerEntry == null)
            {
                Log.outError(LogFilter.Unit, "Unit.HandleDummyAuraProc: Spell {0} has non-existing triggered spell {1}", dummySpell.Id, triggered_spell_id);
                return false;
            }

            CastSpell(target, triggered_spell_id, true, castItem, triggeredByAura);

            return true;
        }

        bool HandleProcTriggerSpell(Unit victim, uint damage, AuraEffect triggeredByAura, SpellInfo procSpell, ProcFlags procFlags, ProcFlagsExLegacy procEx)
        {
            // Get triggered aura spell info
            SpellInfo auraSpellInfo = triggeredByAura.GetSpellInfo();

            // Basepoints of trigger aura
            int triggerAmount = triggeredByAura.GetAmount();

            // Set trigger spell id, target, custom basepoints
            uint trigger_spell_id = triggeredByAura.GetSpellEffectInfo().TriggerSpell;

            Unit target = null;
            int basepoints0 = 0;

            if (triggeredByAura.GetAuraType() == AuraType.ProcTriggerSpellWithValue)
                basepoints0 = triggerAmount;

            Item castItem = !triggeredByAura.GetBase().GetCastItemGUID().IsEmpty() && IsTypeId(TypeId.Player) ? ToPlayer().GetItemByGuid(triggeredByAura.GetBase().GetCastItemGUID()) : null;

            // All ok. Check current trigger spell
            SpellInfo triggerEntry = Global.SpellMgr.GetSpellInfo(trigger_spell_id);
            if (triggerEntry == null)
            {
                // Don't cast unknown spell
                Log.outError(LogFilter.Unit, "Unit.HandleProcTriggerSpell: Spell {0} has 0 in EffectTriggered[{1}]. Unhandled custom case?", auraSpellInfo.Id, triggeredByAura.GetEffIndex());
                return false;
            }

            // not allow proc extra attack spell at extra attack
            if (m_extraAttacks != 0 && triggerEntry.HasEffect(SpellEffectName.AddExtraAttacks))
                return false;

            // Custom basepoints/target for exist spell
            // dummy basepoints or other customs
            switch (trigger_spell_id)
            {
                // Maelstrom Weapon
                case 53817:
                    {
                        // Item - Shaman T10 Enhancement 4P Bonus
                        AuraEffect aurEff = GetAuraEffect(70832, 0);
                        if (aurEff != null)
                        {
                            Aura maelstrom = GetAura(53817);
                            if (maelstrom != null)
                                if ((maelstrom.GetStackAmount() == maelstrom.GetSpellInfo().StackAmount) && RandomHelper.randChance(aurEff.GetAmount()))
                                    CastSpell(this, 70831, true, castItem, triggeredByAura);
                        }
                        break;
                    }
            }

            // extra attack should hit same target
            if (triggerEntry.HasEffect(SpellEffectName.AddExtraAttacks))
                target = victim;

            // try detect target manually if not set
            if (target == null)
                target = !procFlags.HasAnyFlag(ProcFlags.DoneSpellMagicDmgClassPos | ProcFlags.DoneSpellNoneDmgClassPos) && triggerEntry.IsPositive() ? this : victim;

            if (basepoints0 != 0)
                CastCustomSpell(target, trigger_spell_id, basepoints0, 0, 0, true, castItem, triggeredByAura);
            else
                CastSpell(target, trigger_spell_id, true, castItem, triggeredByAura);

            return true;
        }
        bool HandleOverrideClassScriptAuraProc(Unit victim, uint damage, AuraEffect triggeredByAura, SpellInfo procSpell)
        {
            int scriptId = triggeredByAura.GetMiscValue();

            if (victim == null || !victim.IsAlive())
                return false;

            Item castItem = !triggeredByAura.GetBase().GetCastItemGUID().IsEmpty() && IsTypeId(TypeId.Player) ? ToPlayer().GetItemByGuid(triggeredByAura.GetBase().GetCastItemGUID()) : null;

            uint triggered_spell_id = 0;

            switch (scriptId)
            {
                case 4537:                                          // Dreamwalker Raiment 6 pieces bonus
                    triggered_spell_id = 28750;                     // Blessing of the Claw
                    break;
                default:
                    break;
            }

            // not processed
            if (triggered_spell_id == 0)
                return false;

            // standard non-dummy case
            SpellInfo triggerEntry = Global.SpellMgr.GetSpellInfo(triggered_spell_id);

            if (triggerEntry == null)
            {
                Log.outError(LogFilter.Unit, "Unit.HandleOverrideClassScriptAuraProc: Spell {0} triggering for class script id {1}", triggered_spell_id, scriptId);
                return false;
            }

            CastSpell(victim, triggered_spell_id, true, castItem, triggeredByAura); //Do not allow auras to proc from ef

            return true;
        }
        bool HandleAuraRaidProcFromChargeWithValue(AuraEffect triggeredByAura)
        {
            // aura can be deleted at casts
            SpellInfo spellProto = triggeredByAura.GetSpellInfo();
            uint triggered_spell_id = 0;
            int heal = triggeredByAura.GetAmount();
            ObjectGuid caster_guid = triggeredByAura.GetCasterGUID();

            // Currently only Prayer of Mending
            if (triggered_spell_id == 0)
            {
                Log.outDebug(LogFilter.Spells, "Unit.HandleAuraRaidProcFromChargeWithValue, received not handled spell: {0}", spellProto.Id);
                return false;
            }

            // jumps
            int jumps = triggeredByAura.GetBase().GetCharges() - 1;

            // current aura expire
            triggeredByAura.GetBase().SetCharges(1);             // will removed at next charges decrease

            // next target selection
            if (jumps > 0)
            {
                Unit caster = triggeredByAura.GetCaster();
                if (caster != null)
                {
                    SpellEffectInfo effect = triggeredByAura.GetSpellEffectInfo();
                    float radius = effect.CalcRadius(caster);
                    Unit target = GetNextRandomRaidMemberOrPet(radius);
                    if (target != null)
                    {
                        CastCustomSpell(target, spellProto.Id, heal, 0, 0, true, null, triggeredByAura, caster_guid);
                        Aura aura = target.GetAura(spellProto.Id, caster.GetGUID());
                        if (aura != null)
                            aura.SetCharges(jumps);
                    }
                }
            }

            // heal
            CastSpell(this, triggered_spell_id, true, null, triggeredByAura, caster_guid);
            return true;
        }
        bool HandleAuraRaidProcFromCharge(AuraEffect triggeredByAura)
        {
            // aura can be deleted at casts
            SpellInfo spellProto = triggeredByAura.GetSpellInfo();

            uint damageSpellId;
            switch (spellProto.Id)
            {
                case 57949:            // shiver
                    damageSpellId = 57952;
                    break;
                case 59978:            // shiver
                    damageSpellId = 59979;
                    break;
                case 43593:            // Cold Stare
                    damageSpellId = 43594;
                    break;
                default:
                    Log.outError(LogFilter.Unit, "Unit.HandleAuraRaidProcFromCharge, received unhandled spell: {0}", spellProto.Id);
                    return false;
            }

            ObjectGuid caster_guid = triggeredByAura.GetCasterGUID();

            // jumps
            int jumps = triggeredByAura.GetBase().GetCharges() - 1;

            // current aura expire
            triggeredByAura.GetBase().SetCharges(1);             // will removed at next charges decrease

            // next target selection
            if (jumps > 0)
            {
                Unit caster = triggeredByAura.GetCaster();
                if (caster != null)
                {
                    SpellEffectInfo effect = triggeredByAura.GetSpellEffectInfo();
                    float radius = effect.CalcRadius(caster);
                    Unit target = GetNextRandomRaidMemberOrPet(radius);
                    if (target != null)
                    {
                        CastSpell(target, spellProto, true, null, triggeredByAura, caster_guid);
                        Aura aura = target.GetAura(spellProto.Id, caster.GetGUID());
                        if (aura != null)
                            aura.SetCharges(jumps);
                    }
                }
            }

            CastSpell(this, damageSpellId, true, null, triggeredByAura, caster_guid);

            return true;
        }

        public int GetMaxPositiveAuraModifier(AuraType auratype)
        {
            int modifier = 0;

            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var eff in mTotalAuraList)
            {
                if (eff.GetAmount() > modifier)
                    modifier = eff.GetAmount();
            }

            return modifier;
        }

        public int GetMaxNegativeAuraModifier(AuraType auratype)
        {
            int modifier = 0;

            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var eff in mTotalAuraList)
                if (eff.GetAmount() < modifier)
                    modifier = eff.GetAmount();

            return modifier;
        }

        public void ApplyCastTimePercentMod(float val, bool apply)
        {
            if (val > 0)
            {
                ApplyPercentModFloatValue(UnitFields.ModCastSpeed, val, !apply);
                ApplyPercentModFloatValue(UnitFields.ModCastHaste, val, !apply);
                ApplyPercentModFloatValue(UnitFields.ModHasteRegen, val, !apply);
            }
            else
            {
                ApplyPercentModFloatValue(UnitFields.ModCastSpeed, -val, apply);
                ApplyPercentModFloatValue(UnitFields.ModCastHaste, -val, apply);
                ApplyPercentModFloatValue(UnitFields.ModHasteRegen, -val, apply);
            }
        }

        public void RemoveAllGroupBuffsFromCaster(ObjectGuid casterGUID)
        {
            foreach (var iter in m_ownedAuras.KeyValueList)
            {
                Aura aura = iter.Value;
                if (aura.GetCasterGUID() == casterGUID && aura.GetSpellInfo().IsGroupBuff())
                    RemoveOwnedAura(iter);
            }
        }

        public void DelayOwnedAuras(uint spellId, ObjectGuid caster, int delaytime)
        {
            var range = m_ownedAuras.LookupByKey(spellId);
            foreach (var aura in range)
            {
                if (caster.IsEmpty() || aura.GetCasterGUID() == caster)
                {
                    if (aura.GetDuration() < delaytime)
                        aura.SetDuration(0);
                    else
                        aura.SetDuration(aura.GetDuration() - delaytime);

                    // update for out of range group members (on 1 slot use)
                    aura.SetNeedClientUpdateForTargets();
                    Log.outDebug(LogFilter.Spells, "Aura {0} partially interrupted on {1}, new duration: {2} ms", aura.GetId(), GetGUID().ToString(), aura.GetDuration());
                }
            }
        }

        public void CalculateSpellDamageTaken(SpellNonMeleeDamage damageInfo, int damage, SpellInfo spellInfo, WeaponAttackType attackType = WeaponAttackType.BaseAttack, bool crit = false)
        {
            if (damage < 0)
                return;

            Unit victim = damageInfo.target;
            if (victim == null || !victim.IsAlive())
                return;

            SpellSchoolMask damageSchoolMask = damageInfo.schoolMask;

            if (IsDamageReducedByArmor(damageSchoolMask, spellInfo))
                damage = (int)CalcArmorReducedDamage(damageInfo.attacker, victim, (uint)damage, spellInfo, attackType);

            bool blocked = false;
            // Per-school calc
            switch (spellInfo.DmgClass)
            {
                // Melee and Ranged Spells
                case SpellDmgClass.Ranged:
                case SpellDmgClass.Melee:
                    {
                        // Physical Damage
                        if (damageSchoolMask.HasAnyFlag(SpellSchoolMask.Normal))
                        {
                            // Get blocked status
                            blocked = isSpellBlocked(victim, spellInfo, attackType);
                        }

                        if (crit)
                        {
                            damageInfo.HitInfo |= SpellHitType.Crit;

                            // Calculate crit bonus
                            uint crit_bonus = (uint)damage;
                            // Apply crit_damage bonus for melee spells
                            Player modOwner = GetSpellModOwner();
                            if (modOwner != null)
                                modOwner.ApplySpellMod(spellInfo.Id, SpellModOp.CritDamageBonus, ref crit_bonus);
                            damage += (int)crit_bonus;

                            // Apply SPELL_AURA_MOD_ATTACKER_RANGED_CRIT_DAMAGE or SPELL_AURA_MOD_ATTACKER_MELEE_CRIT_DAMAGE
                            float critPctDamageMod = 0.0f;
                            if (attackType == WeaponAttackType.RangedAttack)
                                critPctDamageMod += victim.GetTotalAuraModifier(AuraType.ModAttackerRangedCritDamage);
                            else
                                critPctDamageMod += victim.GetTotalAuraModifier(AuraType.ModAttackerMeleeCritDamage);

                            // Increase crit damage from SPELL_AURA_MOD_CRIT_DAMAGE_BONUS
                            critPctDamageMod += (GetTotalAuraMultiplierByMiscMask(AuraType.ModCritDamageBonus, (uint)spellInfo.GetSchoolMask()) - 1.0f) * 100;

                            if (critPctDamageMod != 0)
                                MathFunctions.AddPct(ref damage, (int)critPctDamageMod);
                        }

                        // Spell weapon based damage CAN BE crit & blocked at same time
                        if (blocked)
                        {
                            // double blocked amount if block is critical
                            uint value = victim.GetBlockPercent();
                            if (victim.isBlockCritical())
                                value *= 2; // double blocked percent
                            damageInfo.blocked = (uint)MathFunctions.CalculatePct(damage, value);
                            damage -= (int)damageInfo.blocked;
                        }
                        uint dmg = (uint)damage;
                        ApplyResilience(victim, ref dmg);
                        damage = (int)dmg;
                        break;
                    }
                // Magical Attacks
                case SpellDmgClass.None:
                case SpellDmgClass.Magic:
                    {
                        // If crit add critical bonus
                        if (crit)
                        {
                            damageInfo.HitInfo |= SpellHitType.Crit;
                            damage = (int)SpellCriticalDamageBonus(spellInfo, (uint)damage, victim);
                        }
                        uint dmg = (uint)damage;
                        ApplyResilience(victim, ref dmg);
                        damage = (int)dmg;
                        break;
                    }
                default:
                    break;
            }

            // Script Hook For CalculateSpellDamageTaken -- Allow scripts to change the Damage post class mitigation calculations
            Global.ScriptMgr.ModifySpellDamageTaken(damageInfo.target, damageInfo.attacker, ref damage);

            // Calculate absorb resist
            if (damage > 0)
            {
                CalcAbsorbResist(victim, damageSchoolMask, DamageEffectType.SpellDirect, (uint)damage, ref damageInfo.absorb, ref damageInfo.resist, spellInfo);
                damage -= (int)(damageInfo.absorb + damageInfo.resist);
            }
            else
                damage = 0;

            damageInfo.damage = (uint)damage;
        }
        public void DealSpellDamage(SpellNonMeleeDamage damageInfo, bool durabilityLoss)
        {
            if (damageInfo == null)
                return;

            Unit victim = damageInfo.target;
            if (victim == null)
                return;

            if (!victim.IsAlive() || victim.HasUnitState(UnitState.InFlight) || (victim.IsTypeId(TypeId.Unit) && victim.ToCreature().IsEvadingAttacks()))
                return;

            SpellInfo spellProto = Global.SpellMgr.GetSpellInfo(damageInfo.SpellId);
            if (spellProto == null)
            {
                Log.outDebug(LogFilter.Unit, "Unit.DealSpellDamage has wrong damageInfo.SpellID: {0}", damageInfo.SpellId);
                return;
            }

            // Call default DealDamage
            CleanDamage cleanDamage = new CleanDamage(damageInfo.cleanDamage, damageInfo.absorb, WeaponAttackType.BaseAttack, MeleeHitOutcome.Normal);
            DealDamage(victim, damageInfo.damage, cleanDamage, DamageEffectType.SpellDirect, damageInfo.schoolMask, spellProto, durabilityLoss);
        }

        public void SendSpellNonMeleeDamageLog(SpellNonMeleeDamage log)
        {
            SpellNonMeleeDamageLog packet = new SpellNonMeleeDamageLog();
            packet.Me = log.target.GetGUID();
            packet.CasterGUID = log.attacker.GetGUID();
            packet.CastID = log.castId;
            packet.SpellID = (int)log.SpellId;
            packet.Damage = (int)log.damage;
            if (log.damage > log.preHitHealth)
                packet.Overkill = (int)(log.damage - log.preHitHealth);
            else
                packet.Overkill = 0;

            packet.SchoolMask = (byte)log.schoolMask;
            packet.ShieldBlock = (int)log.blocked;
            packet.Resisted = (int)log.resist;
            packet.Absorbed = (int)log.absorb;
            packet.Periodic = log.periodicLog;
            packet.Flags = (int)log.HitInfo;
            SendCombatLogMessage(packet);
        }

        public void SendPeriodicAuraLog(SpellPeriodicAuraLogInfo info)
        {
            AuraEffect aura = info.auraEff;

            SpellPeriodicAuraLog data = new SpellPeriodicAuraLog();
            data.TargetGUID = GetGUID();
            data.CasterGUID = aura.GetCasterGUID();
            data.SpellID = aura.GetId();
            data.LogData.Initialize(this);

            SpellPeriodicAuraLog.SpellLogEffect spellLogEffect = new SpellPeriodicAuraLog.SpellLogEffect();
            spellLogEffect.Effect = (uint)aura.GetAuraType();
            spellLogEffect.Amount = info.damage;
            spellLogEffect.OverHealOrKill = (uint)info.overDamage;
            spellLogEffect.SchoolMaskOrPower = (uint)aura.GetSpellInfo().GetSchoolMask();
            spellLogEffect.AbsorbedOrAmplitude = info.absorb;
            spellLogEffect.Resisted = info.resist;
            spellLogEffect.Crit = info.critical;
            /// @todo: implement debug info

            data.Effects.Add(spellLogEffect);

            SendCombatLogMessage(data);
        }
        public void SendSpellMiss(Unit target, uint spellID, SpellMissInfo missInfo)
        {
            SpellMissLog spellMissLog = new SpellMissLog();
            spellMissLog.SpellID = spellID;
            spellMissLog.Caster = GetGUID();
            spellMissLog.Entries.Add(new SpellLogMissEntry(target.GetGUID(), (byte)missInfo));
            SendMessageToSet(spellMissLog, true);
        }

        void SendSpellDamageResist(Unit target, uint spellId)
        {
            ProcResist procResist = new ProcResist();
            procResist.Caster = GetGUID();
            procResist.SpellID = spellId;
            procResist.Target = target.GetGUID();
            SendMessageToSet(procResist, true);
        }

        public void SendSpellDamageImmune(Unit target, uint spellId, bool isPeriodic)
        {
            SpellOrDamageImmune spellOrDamageImmune = new SpellOrDamageImmune();
            spellOrDamageImmune.CasterGUID = GetGUID();
            spellOrDamageImmune.VictimGUID = target.GetGUID();
            spellOrDamageImmune.SpellID = spellId;
            spellOrDamageImmune.IsPeriodic = isPeriodic;
            SendMessageToSet(spellOrDamageImmune, true);
        }

        public void SendSpellInstakillLog(uint spellId, Unit caster, Unit target = null)
        {
            SpellInstakillLog spellInstakillLog = new SpellInstakillLog();
            spellInstakillLog.Caster = caster.GetGUID();
            spellInstakillLog.Target = target ? target.GetGUID() : caster.GetGUID();
            spellInstakillLog.SpellID = spellId;
            SendMessageToSet(spellInstakillLog, false);
        }

        public void RemoveAurasOnEvade()
        {
            if (IsCharmedOwnedByPlayerOrPlayer()) // if it is a player owned creature it should not remove the aura
                return;

            // don't remove vehicle auras, passengers aren't supposed to drop off the vehicle
            // don't remove clone caster on evade (to be verified)
            RemoveAllAurasExceptType(AuraType.ControlVehicle, AuraType.CloneCaster);
        }

        public void RemoveAllAurasOnDeath()
        {
            // used just after dieing to remove all visible auras
            // and disable the mods for the passive ones
            foreach (var app in GetAppliedAuras())
            {
                if (app.Value == null)
                    continue;

                Aura aura = app.Value.GetBase();
                if (!aura.IsPassive() && !aura.IsDeathPersistent())
                    _UnapplyAura(app, AuraRemoveMode.ByDeath);
            }

            foreach (var pair in GetOwnedAuras())
            {
                Aura aura = pair.Value;
                if (pair.Value == null)
                    continue;

                if (!aura.IsPassive() && !aura.IsDeathPersistent())
                    RemoveOwnedAura(pair, AuraRemoveMode.ByDeath);
            }
        }
        public void RemoveMovementImpairingAuras()
        {
            RemoveAurasWithMechanic((1 << (int)Mechanics.Snare) | (1 << (int)Mechanics.Root));
        }
        public void RemoveAllAurasRequiringDeadTarget()
        {
            foreach (var app in GetAppliedAuras())
            {
                Aura aura = app.Value.GetBase();
                if (!aura.IsPassive() && aura.GetSpellInfo().IsRequiringDeadTarget())
                    _UnapplyAura(app, AuraRemoveMode.Default);
            }

            foreach (var aura in GetOwnedAuras())
            {
                if (!aura.Value.IsPassive() && aura.Value.GetSpellInfo().IsRequiringDeadTarget())
                    RemoveOwnedAura(aura, AuraRemoveMode.Default);
            }
        }

        public AuraEffect IsScriptOverriden(SpellInfo spell, int script)
        {
            var auras = GetAuraEffectsByType(AuraType.OverrideClassScripts);
            foreach (var eff in auras)
            {
                if (eff.GetMiscValue() == script)
                    if (eff.IsAffectingSpell(spell))
                        return eff;
            }
            return null;
        }

        public void ApplySpellDispelImmunity(SpellInfo spellProto, DispelType type, bool apply)
        {
            ApplySpellImmune(spellProto.Id, SpellImmunity.Dispel, type, apply);

            if (apply && spellProto.HasAttribute(SpellAttr1.DispelAurasOnImmunity))
            {
                // Create dispel mask by dispel type
                uint dispelMask = SpellInfo.GetDispelMask(type);
                // Dispel all existing auras vs current dispel type
                var auras = GetAppliedAuras();
                foreach (var pair in auras)
                {
                    SpellInfo spell = pair.Value.GetBase().GetSpellInfo();
                    if ((spell.GetDispelMask() & dispelMask) != 0)
                    {
                        // Dispel aura
                        RemoveAura(pair);
                    }
                }
            }
        }

        public bool isNonTriggerAura(AuraType type)
        {
            switch (type)
            {
                case AuraType.ModPowerRegen:
                case AuraType.ReducePushback:
                    return true;
            }
            return false;
        }
        public bool isTriggerAura(AuraType type)
        {
            switch (type)
            {
                case AuraType.ProcOnPowerAmount:
                case AuraType.ProcOnPowerAmount2:
                case AuraType.Dummy:
                case AuraType.ModConfuse:
                case AuraType.ModThreat:
                case AuraType.ModStun:
                case AuraType.ModDamageDone:
                case AuraType.ModDamageTaken:
                case AuraType.ModResistance:
                case AuraType.ModStealth:
                case AuraType.ModFear:
                case AuraType.ModRoot:
                case AuraType.Transform:
                case AuraType.ReflectSpells:
                case AuraType.DamageImmunity:
                case AuraType.ProcTriggerSpell:
                case AuraType.ProcTriggerDamage:
                case AuraType.ModCastingSpeedNotStack:
                case AuraType.SchoolAbsorb:
                case AuraType.ModPowerCostSchoolPct:
                case AuraType.ModPowerCostSchool:
                case AuraType.ReflectSpellsSchool:
                case AuraType.MechanicImmunity:
                case AuraType.ModDamagePercentTaken:
                case AuraType.SpellMagnet:
                case AuraType.ModAttackPower:
                case AuraType.ModPowerRegenPercent:
                case AuraType.AddCasterHitTrigger:
                case AuraType.OverrideClassScripts:
                case AuraType.ModMechanicResistance:
                case AuraType.MeleeAttackPowerAttackerBonus:
                case AuraType.ModMeleeHaste:
                case AuraType.ModMeleeHaste3:
                case AuraType.ModAttackerMeleeHitChance:
                case AuraType.RaidProcFromCharge:
                case AuraType.RaidProcFromChargeWithValue:
                case AuraType.ProcTriggerSpellWithValue:
                case AuraType.ModSpellDamageFromCaster:
                case AuraType.AbilityIgnoreAurastate:
                case AuraType.ModRoot2:
                    return true;
            }
            return false;
        }
        public bool isAlwaysTriggeredAura(AuraType type)
        {
            switch (type)
            {
                case AuraType.OverrideClassScripts:
                case AuraType.ModFear:
                case AuraType.ModRoot:
                case AuraType.ModStun:
                case AuraType.Transform:
                case AuraType.SpellMagnet:
                case AuraType.SchoolAbsorb:
                case AuraType.ModStealth:
                case AuraType.ModRoot2:
                    return true;
            }
            return false;
        }

        public DiminishingLevels GetDiminishing(DiminishingGroup group)
        {
            foreach (var dim in m_Diminishing)
            {
                if (dim.DRGroup != group)
                    continue;

                if (dim.hitCount == 0)
                    return DiminishingLevels.Level1;

                if (dim.hitTime == 0)
                    return DiminishingLevels.Level1;

                // If last spell was casted more than 18 seconds ago - reset the count.
                if (dim.stack == 0 && Time.GetMSTimeDiff(dim.hitTime, Time.GetMSTime()) > 18 * Time.InMilliseconds)
                {
                    dim.hitCount = DiminishingLevels.Level1;
                    return DiminishingLevels.Level1;
                }
                // or else increase the count.
                else
                    return dim.hitCount;
            }
            return DiminishingLevels.Level1;
        }

        public void IncrDiminishing(DiminishingGroup group)
        {
            // Checking for existing in the table
            foreach (var dim in m_Diminishing)
            {
                if (dim.DRGroup != group)
                    continue;
                if (dim.hitCount < Global.SpellMgr.GetDiminishingReturnsMaxLevel(group))
                    dim.hitCount += 1;
                return;
            }
            m_Diminishing.Add(new DiminishingReturn(group, Time.GetMSTime(), DiminishingLevels.Level2));
        }

        public float ApplyDiminishingToDuration(DiminishingGroup group, int duration, Unit caster, DiminishingLevels Level, int limitduration)
        {
            if (duration == -1 || group == DiminishingGroup.None)
                return 1.0f;

            // test pet/charm masters instead pets/charmeds
            Unit targetOwner = GetCharmerOrOwner();
            Unit casterOwner = caster.GetCharmerOrOwner();

            if (limitduration > 0 && duration > limitduration)
            {
                Unit target = targetOwner ?? this;
                Unit source = casterOwner ?? caster;

                if ((target.IsTypeId(TypeId.Player) || target.ToCreature().GetCreatureTemplate().FlagsExtra.HasAnyFlag(CreatureFlagsExtra.AllDiminish))
                    && source.IsTypeId(TypeId.Player))
                    duration = limitduration;
            }

            float mod = 1.0f;

            switch (group)
            {
                case DiminishingGroup.Taunt:
                    if (IsTypeId(TypeId.Unit) && ToCreature().GetCreatureTemplate().FlagsExtra.HasAnyFlag(CreatureFlagsExtra.TauntDiminish))
                    {
                        DiminishingLevels diminish = Level;
                        switch (diminish)
                        {
                            case DiminishingLevels.Level1:
                                break;
                            case DiminishingLevels.Level2:
                                mod = 0.65f;
                                break;
                            case DiminishingLevels.Level3:
                                mod = 0.4225f;
                                break;
                            case DiminishingLevels.Level4:
                                mod = 0.274625f;
                                break;
                            case DiminishingLevels.TauntImmune:
                                mod = 0.0f;
                                break;
                            default:
                                break;
                        }
                    }
                    break;
                case DiminishingGroup.AOEKnockback:
                    if ((Global.SpellMgr.GetDiminishingReturnsGroupType(group) == DiminishingReturnsType.Player && (((targetOwner ? targetOwner : this).ToPlayer())
                        || IsTypeId(TypeId.Unit) && ToCreature().GetCreatureTemplate().FlagsExtra.HasAnyFlag(CreatureFlagsExtra.AllDiminish)))
                        || Global.SpellMgr.GetDiminishingReturnsGroupType(group) == DiminishingReturnsType.All)
                    {
                        DiminishingLevels diminish = Level;
                        switch (diminish)
                        {
                            case DiminishingLevels.Level1:
                                break;
                            case DiminishingLevels.Level2:
                                mod = 0.5f;
                                break;
                            default:
                                break;
                        }
                    }
                    break;
                default:
                    if ((Global.SpellMgr.GetDiminishingReturnsGroupType(group) == DiminishingReturnsType.Player && (((targetOwner ? targetOwner : this).ToPlayer())
                        || IsTypeId(TypeId.Unit) && ToCreature().GetCreatureTemplate().FlagsExtra.HasAnyFlag(CreatureFlagsExtra.AllDiminish)))
                        || Global.SpellMgr.GetDiminishingReturnsGroupType(group) == DiminishingReturnsType.All)
                    {
                        DiminishingLevels diminish = Level;
                        switch (diminish)
                        {
                            case DiminishingLevels.Level1:
                                break;
                            case DiminishingLevels.Level2:
                                mod = 0.5f;
                                break;
                            case DiminishingLevels.Level3:
                                mod = 0.25f;
                                break;
                            case DiminishingLevels.Immune:
                                mod = 0.0f;
                                break;
                            default: break;
                        }
                    }
                    break;
            }

            duration = (int)(duration * mod);
            return mod;
        }

        public void ApplyDiminishingAura(DiminishingGroup group, bool apply)
        {
            // Checking for existing in the table
            foreach (var dim in m_Diminishing)
            {
                if (dim.DRGroup != group)
                    continue;

                if (apply)
                    dim.stack += 1;
                else if (dim.stack != 0)
                {
                    dim.stack -= 1;
                    // Remember time after last aura from group removed
                    if (dim.stack == 0)
                        dim.hitTime = Time.GetMSTime();
                }
                break;
            }
        }

        public uint GetRemainingPeriodicAmount(ObjectGuid caster, uint spellId, AuraType auraType, int effectIndex = 0)
        {
            uint amount = 0;
            var periodicAuras = GetAuraEffectsByType(auraType);
            foreach (var eff in periodicAuras)
            {
                if (eff.GetCasterGUID() != caster || eff.GetId() != spellId || eff.GetEffIndex() != effectIndex || eff.GetTotalTicks() == 0)
                    continue;
                amount += (uint)((eff.GetAmount() * Math.Max(eff.GetTotalTicks() - eff.GetTickNumber(), 0)) / eff.GetTotalTicks());
                break;
            }

            return amount;
        }

        // Interrupts
        public void InterruptNonMeleeSpells(bool withDelayed, uint spell_id = 0, bool withInstant = true)
        {
            // generic spells are interrupted if they are not finished or delayed
            if (GetCurrentSpell(CurrentSpellTypes.Generic) != null && (spell_id == 0 || m_currentSpells[CurrentSpellTypes.Generic].m_spellInfo.Id == spell_id))
                InterruptSpell(CurrentSpellTypes.Generic, withDelayed, withInstant);

            // autorepeat spells are interrupted if they are not finished or delayed
            if (GetCurrentSpell(CurrentSpellTypes.AutoRepeat) != null && (spell_id == 0 || m_currentSpells[CurrentSpellTypes.AutoRepeat].m_spellInfo.Id == spell_id))
                InterruptSpell(CurrentSpellTypes.AutoRepeat, withDelayed, withInstant);

            // channeled spells are interrupted if they are not finished, even if they are delayed
            if (GetCurrentSpell(CurrentSpellTypes.Channeled) != null && (spell_id == 0 || m_currentSpells[CurrentSpellTypes.Channeled].m_spellInfo.Id == spell_id))
                InterruptSpell(CurrentSpellTypes.Channeled, true, true);
        }
        public void InterruptSpell(CurrentSpellTypes spellType, bool withDelayed = true, bool withInstant = true)
        {
            Contract.Assert(spellType < CurrentSpellTypes.Max);

            Log.outDebug(LogFilter.Unit, "Interrupt spell for unit {0}", GetEntry());
            Spell spell = m_currentSpells.LookupByKey(spellType);
            if (spell != null
                && (withDelayed || spell.getState() != SpellState.Delayed)
                && (withInstant || spell.GetCastTime() > 0))
            {
                // for example, do not let self-stun aura interrupt itself
                if (!spell.IsInterruptable())
                    return;

                // send autorepeat cancel message for autorepeat spells
                if (spellType == CurrentSpellTypes.AutoRepeat)
                    if (IsTypeId(TypeId.Player))
                        ToPlayer().SendAutoRepeatCancel(this);

                if (spell.getState() != SpellState.Finished)
                    spell.cancel();

                m_currentSpells[spellType] = null;
                spell.SetReferencedFromCurrent(false);
            }
        }
        public void UpdateInterruptMask()
        {
            m_interruptMask = 0;
            foreach (var app in m_interruptableAuras)
                m_interruptMask |= (uint)app.GetBase().GetSpellInfo().AuraInterruptFlags;

            Spell spell = GetCurrentSpell(CurrentSpellTypes.Channeled);
            if (spell != null)
                if (spell.getState() == SpellState.Casting)
                    m_interruptMask |= (uint)spell.m_spellInfo.ChannelInterruptFlags;
        }

        // Auras
        public List<Aura> GetSingleCastAuras() { return m_scAuras; }
        public List<KeyValuePair<uint, Aura>> GetOwnedAuras()
        {
            return m_ownedAuras.KeyValueList;
        }
        public List<KeyValuePair<uint, AuraApplication>> GetAppliedAuras()
        {
            return m_appliedAuras.KeyValueList;
        }

        public Aura AddAura(uint spellId, Unit target)
        {
            if (target == null)
                return null;

            SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(spellId);
            if (spellInfo == null)
                return null;

            if (!target.IsAlive() && !spellInfo.IsPassive() && !spellInfo.HasAttribute(SpellAttr2.CanTargetDead))
                return null;

            return AddAura(spellInfo, SpellConst.MaxEffectMask, target);
        }

        public Aura AddAura(SpellInfo spellInfo, uint effMask, Unit target)
        {
            if (spellInfo == null)
                return null;

            if (target.IsImmunedToSpell(spellInfo))
                return null;

            for (byte i = 0; i < SpellConst.MaxEffects; ++i)
            {
                if (!Convert.ToBoolean(effMask & (1 << i)))
                    continue;
                if (target.IsImmunedToSpellEffect(spellInfo, i))
                    effMask &= ~(uint)(1 << i);
            }

            ObjectGuid castId = ObjectGuid.Create(HighGuid.Cast, SpellCastSource.Normal, GetMapId(), spellInfo.Id, GetMap().GenerateLowGuid(HighGuid.Cast));
            Aura aura = Aura.TryRefreshStackOrCreate(spellInfo, castId, effMask, target, this);
            if (aura != null)
            {
                aura.ApplyForTargets();
                return aura;
            }
            return null;
        }

        public bool HandleSpellClick(Unit clicker, sbyte seatId = 0)
        {
            bool result = false;

            uint spellClickEntry = GetVehicleKit() != null ? GetVehicleKit().GetCreatureEntry() : GetEntry();
            var clickPair = Global.ObjectMgr.GetSpellClickInfoMapBounds(spellClickEntry);
            foreach (var clickInfo in clickPair)
            {
                //! First check simple relations from clicker to clickee
                if (!clickInfo.IsFitToRequirements(clicker, this))
                    continue;

                //! Check database conditions
                if (!Global.ConditionMgr.IsObjectMeetingSpellClickConditions(spellClickEntry, clickInfo.spellId, clicker, this))
                    continue;

                Unit caster = Convert.ToBoolean(clickInfo.castFlags & (byte)SpellClickCastFlags.CasterClicker) ? clicker : this;
                Unit target = Convert.ToBoolean(clickInfo.castFlags & (byte)SpellClickCastFlags.TargetClicker) ? clicker : this;
                ObjectGuid origCasterGUID = Convert.ToBoolean(clickInfo.castFlags & (byte)SpellClickCastFlags.OrigCasterOwner) ? GetOwnerGUID() : clicker.GetGUID();

                SpellInfo spellEntry = Global.SpellMgr.GetSpellInfo(clickInfo.spellId);
                // if (!spellEntry) should be checked at npc_spellclick load

                if (seatId > -1)
                {
                    byte i = 0;
                    bool valid = false;
                    foreach (SpellEffectInfo effect in spellEntry.GetEffectsForDifficulty(GetMap().GetDifficultyID()))
                    {
                        if (effect == null)
                            continue;

                        if (effect.ApplyAuraName == AuraType.ControlVehicle)
                        {
                            valid = true;
                            break;
                        }
                        ++i;
                    }

                    if (!valid)
                    {
                        Log.outError(LogFilter.Sql, "Spell {0} specified in npc_spellclick_spells is not a valid vehicle enter aura!", clickInfo.spellId);
                        continue;
                    }

                    if (IsInMap(caster))
                        caster.CastCustomSpell(clickInfo.spellId, SpellValueMod.BasePoint0 + i, seatId + 1, target, GetVehicleKit() != null ? TriggerCastFlags.IgnoreCasterMountedOrOnVehicle : TriggerCastFlags.None, null, null, origCasterGUID);
                    else    // This can happen during Player._LoadAuras
                    {
                        int[] bp0 = new int[SpellConst.MaxEffects];
                        foreach (SpellEffectInfo effect in spellEntry.GetEffectsForDifficulty(GetMap().GetDifficultyID()))
                        {
                            if (effect != null)
                                bp0[effect.EffectIndex] = effect.BasePoints;
                        }

                        bp0[i] = seatId;
                        Aura.TryRefreshStackOrCreate(spellEntry, ObjectGuid.Create(HighGuid.Cast, SpellCastSource.Normal, GetMapId(), spellEntry.Id, GetMap().GenerateLowGuid(HighGuid.Cast)), SpellConst.MaxEffectMask, this, clicker, bp0, null, origCasterGUID);
                    }
                }
                else
                {
                    if (IsInMap(caster))
                        caster.CastSpell(target, spellEntry, GetVehicleKit() != null ? TriggerCastFlags.IgnoreCasterMountedOrOnVehicle : TriggerCastFlags.None, null, null, origCasterGUID);
                    else
                        Aura.TryRefreshStackOrCreate(spellEntry, ObjectGuid.Create(HighGuid.Cast, SpellCastSource.Normal, GetMapId(), spellEntry.Id, GetMap().GenerateLowGuid(HighGuid.Cast)), SpellConst.MaxEffectMask, this, clicker, null, null, origCasterGUID);
                }

                result = true;
            }

            Creature creature = ToCreature();
            if (creature && creature.IsAIEnabled)
                creature.GetAI().OnSpellClick(clicker, ref result);

            return result;
        }

        public float GetTotalAuraMultiplierByMiscMask(AuraType auratype, uint miscMask)
        {
            Dictionary<SpellGroup, int> SameEffectSpellGroup = new Dictionary<SpellGroup, int>();
            float multiplier = 1.0f;

            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var eff in mTotalAuraList)
            {
                if (eff.GetMiscValue().HasAnyFlag((int)miscMask))
                {
                    // Check if the Aura Effect has a the Same Effect Stack Rule and if so, use the highest amount of that SpellGroup
                    // If the Aura Effect does not have this Stack Rule, it returns false so we can add to the multiplier as usual
                    if (!Global.SpellMgr.AddSameEffectStackRuleSpellGroups(eff.GetSpellInfo(), eff.GetAmount(), out SameEffectSpellGroup))
                        MathFunctions.AddPct(ref multiplier, eff.GetAmount());
                }
            }
            // Add the highest of the Same Effect Stack Rule SpellGroups to the multiplier
            foreach (var group in SameEffectSpellGroup.Values)
                MathFunctions.AddPct(ref multiplier, group);

            return multiplier;
        }

        public bool HasAura(uint spellId, ObjectGuid casterGUID = default(ObjectGuid), ObjectGuid itemCasterGUID = default(ObjectGuid), uint reqEffMask = 0)
        {
            if (GetAuraApplication(spellId, casterGUID, itemCasterGUID, reqEffMask) != null)
                return true;
            return false;
        }
        public bool HasAuraEffect(uint spellId, uint effIndex, ObjectGuid casterGUID = default(ObjectGuid))
        {
            var range = m_appliedAuras.LookupByKey(spellId);
            if (!range.Empty())
            {
                foreach (var aura in range)
                    if (aura.HasEffect(effIndex) && (casterGUID.IsEmpty() || aura.GetBase().GetCasterGUID() == casterGUID))
                        return true;
            }

            return false;
        }
        public bool HasAuraWithMechanic(uint mechanicMask)
        {
            foreach (var pair in GetAppliedAuras())
            {
                SpellInfo spellInfo = pair.Value.GetBase().GetSpellInfo();
                if (spellInfo.Mechanic != 0 && Convert.ToBoolean(mechanicMask & (1 << (int)spellInfo.Mechanic)))
                    return true;

                foreach (SpellEffectInfo effect in pair.Value.GetBase().GetSpellEffectInfos())
                    if (effect != null && effect.Effect != 0 && effect.Mechanic != 0)
                        if (Convert.ToBoolean(mechanicMask & (1 << (int)effect.Mechanic)))
                            return true;
            }

            return false;
        }

        int GetMaxPositiveAuraModifierByMiscValue(AuraType auratype, int miscValue)
        {
            int modifier = 0;

            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var eff in mTotalAuraList)
            {
                if (eff.GetMiscValue() == miscValue && eff.GetAmount() > modifier)
                    modifier = eff.GetAmount();
            }

            return modifier;
        }

        int GetMaxNegativeAuraModifierByMiscValue(AuraType auratype, int miscValue)
        {
            int modifier = 0;

            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var eff in mTotalAuraList)
            {
                if (eff.GetMiscValue() == miscValue && eff.GetAmount() < modifier)
                    modifier = eff.GetAmount();
            }

            return modifier;
        }

        // target dependent range checks
        public float GetSpellMaxRangeForTarget(Unit target, SpellInfo spellInfo)
        {
            if (spellInfo.RangeEntry == null)
                return 0;
            if (spellInfo.RangeEntry.MaxRangeFriend == spellInfo.RangeEntry.MaxRangeHostile)
                return spellInfo.GetMaxRange();
            if (!target)
                return spellInfo.GetMaxRange(true);
            return spellInfo.GetMaxRange(!IsHostileTo(target));
        }

        public float GetSpellMinRangeForTarget(Unit target, SpellInfo spellInfo)
        {
            if (spellInfo.RangeEntry == null)
                return 0;
            if (spellInfo.RangeEntry.MinRangeFriend == spellInfo.RangeEntry.MinRangeHostile)
                return spellInfo.GetMinRange();
            if (!target)
                return spellInfo.GetMinRange(true);
            return spellInfo.GetMinRange(!IsHostileTo(target));
        }

        public bool HasAuraType(AuraType auraType)
        {
            return !m_modAuras.LookupByKey(auraType).Empty();
        }
        public bool HasAuraTypeWithCaster(AuraType auratype, ObjectGuid caster)
        {
            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var aura in mTotalAuraList)
                if (caster == aura.GetCasterGUID())
                    return true;
            return false;
        }
        public bool HasAuraTypeWithMiscvalue(AuraType auratype, int miscvalue)
        {
            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var aura in mTotalAuraList)
                if (miscvalue == aura.GetMiscValue())
                    return true;
            return false;
        }
        public bool HasAuraTypeWithAffectMask(AuraType auratype, SpellInfo affectedSpell)
        {
            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var aura in mTotalAuraList)
                if (aura.IsAffectingSpell(affectedSpell))
                    return true;
            return false;
        }
        public bool HasAuraTypeWithValue(AuraType auratype, int value)
        {
            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var aura in mTotalAuraList)
                if (value == aura.GetAmount())
                    return true;
            return false;
        }

        public bool HasNegativeAuraWithInterruptFlag(uint flag, ObjectGuid guid = default(ObjectGuid))
        {
            if (!Convert.ToBoolean(m_interruptMask & flag))
                return false;
            foreach (var aura in m_interruptableAuras)
            {
                if (!aura.IsPositive() && Convert.ToBoolean((uint)aura.GetBase().GetSpellInfo().AuraInterruptFlags & flag)
                    && (guid.IsEmpty() || aura.GetBase().GetCasterGUID() == guid))
                    return true;
            }
            return false;
        }
        bool HasNegativeAuraWithAttribute(SpellAttr0 flag, ObjectGuid guid = default(ObjectGuid))
        {
            foreach (var list in GetAppliedAuras())
            {
                Aura aura = list.Value.GetBase();
                if (!list.Value.IsPositive() && aura.GetSpellInfo().HasAttribute(flag) && (guid.IsEmpty() || aura.GetCasterGUID() == guid))
                    return true;
            }
            return false;
        }

        public uint GetAuraCount(uint spellId)
        {
            uint count = 0;
            var range = m_appliedAuras.LookupByKey(spellId);
            foreach (var aura in range)
            {
                if (aura.GetBase().GetStackAmount() == 0)
                    ++count;
                else
                    count += aura.GetBase().GetStackAmount();
            }

            return count;
        }
        public Aura GetAuraOfRankedSpell(uint spellId, ObjectGuid casterGUID = default(ObjectGuid), ObjectGuid itemCasterGUID = default(ObjectGuid), uint reqEffMask = 0)
        {
            var aurApp = GetAuraApplicationOfRankedSpell(spellId, casterGUID, itemCasterGUID, reqEffMask);
            return aurApp?.GetBase();
        }
        AuraApplication GetAuraApplicationOfRankedSpell(uint spellId, ObjectGuid casterGUID = default(ObjectGuid), ObjectGuid itemCasterGUID = default(ObjectGuid), uint reqEffMask = 0, AuraApplication except = null)
        {
            uint rankSpell = Global.SpellMgr.GetFirstSpellInChain(spellId);
            while (rankSpell != 0)
            {
                AuraApplication aurApp = GetAuraApplication(rankSpell, casterGUID, itemCasterGUID, reqEffMask, except);
                if (aurApp != null)
                    return aurApp;
                rankSpell = Global.SpellMgr.GetNextSpellInChain(rankSpell);
            }
            return null;
        }

        public List<DispelCharges> GetDispellableAuraList(Unit caster, uint dispelMask)
        {
            List<DispelCharges> dispelList = new List<DispelCharges>();

            var auras = GetOwnedAuras();
            foreach (var pair in auras)
            {
                Aura aura = pair.Value;
                AuraApplication aurApp = aura.GetApplicationOfTarget(GetGUID());
                if (aurApp == null)
                    continue;

                // don't try to remove passive auras
                if (aura.IsPassive())
                    continue;

                if (Convert.ToBoolean(aura.GetSpellInfo().GetDispelMask() & dispelMask))
                {
                    // do not remove positive auras if friendly target
                    //               negative auras if non-friendly target
                    if (aurApp.IsPositive() == IsFriendlyTo(caster))
                        continue;

                    // The charges / stack amounts don't count towards the total number of auras that can be dispelled.
                    // Ie: A dispel on a target with 5 stacks of Winters Chill and a Polymorph has 1 / (1 + 1) -> 50% chance to dispell
                    // Polymorph instead of 1 / (5 + 1) -> 16%.
                    bool dispelCharges = aura.GetSpellInfo().HasAttribute(SpellAttr7.DispelCharges);
                    byte charges = dispelCharges ? aura.GetCharges() : aura.GetStackAmount();
                    if (charges > 0)
                        dispelList.Add(new DispelCharges(aura, charges));
                }
            }

            return dispelList;
        }

        public void RemoveAurasWithInterruptFlags(SpellAuraInterruptFlags flag, uint except = 0)
        {
            if (!Convert.ToBoolean(m_interruptMask & (uint)flag))
                return;

            // interrupt auras
            for (var i = 0; i < m_interruptableAuras.Count; i++)
            {
                Aura aura = m_interruptableAuras[i].GetBase();

                if (Convert.ToBoolean(aura.GetSpellInfo().AuraInterruptFlags & flag) && (except == 0 || aura.GetId() != except)
                    && !(Convert.ToBoolean(flag & SpellAuraInterruptFlags.Move) && HasAuraTypeWithAffectMask(AuraType.CastWhileWalking, aura.GetSpellInfo())))
                {
                    uint removedAuras = m_removedAurasCount;
                    RemoveAura(aura);
                    if (m_removedAurasCount > removedAuras + 1)
                        i = 0;
                }
            }

            // interrupt channeled spell
            Spell spell = GetCurrentSpell(CurrentSpellTypes.Channeled);
            if (spell != null)
                if (spell.getState() == SpellState.Casting
                    && Convert.ToBoolean((uint)spell.m_spellInfo.ChannelInterruptFlags & (uint)flag)
                    && spell.m_spellInfo.Id != except
                    && !(Convert.ToBoolean(flag & SpellAuraInterruptFlags.Move) && HasAuraTypeWithAffectMask(AuraType.CastWhileWalking, spell.GetSpellInfo())))
                    InterruptNonMeleeSpells(false);

            UpdateInterruptMask();
        }
        public void RemoveAurasWithMechanic(uint mechanic_mask, AuraRemoveMode removemode = AuraRemoveMode.Default, uint except = 0)
        {
            foreach (var app in GetAppliedAuras())
            {
                if (app.Value == null)
                    continue;
                Aura aura = app.Value.GetBase();
                if (except == 0 || aura.GetId() != except)
                {
                    if (Convert.ToBoolean(aura.GetSpellInfo().GetAllEffectsMechanicMask() & mechanic_mask))
                    {
                        RemoveAura(app, removemode);
                        continue;
                    }
                }
            }
        }
        public void RemoveAurasDueToSpellBySteal(uint spellId, ObjectGuid casterGUID, Unit stealer)
        {
            var range = m_ownedAuras.LookupByKey(spellId);
            foreach (var aura in range)
            {
                if (aura.GetCasterGUID() == casterGUID)
                {
                    int[] damage = new int[SpellConst.MaxEffects];
                    int[] baseDamage = new int[SpellConst.MaxEffects];
                    uint effMask = 0;
                    uint recalculateMask = 0;
                    Unit caster = aura.GetCaster();
                    for (byte i = 0; i < SpellConst.MaxEffects; ++i)
                    {
                        if (aura.GetEffect(i) != null)
                        {
                            baseDamage[i] = aura.GetEffect(i).GetBaseAmount();
                            damage[i] = aura.GetEffect(i).GetAmount();
                            effMask |= 1u << i;
                            if (aura.GetEffect(i).CanBeRecalculated())
                                recalculateMask |= 1u << i;
                        }
                        else
                        {
                            baseDamage[i] = 0;
                            damage[i] = 0;
                        }
                    }

                    bool stealCharge = aura.GetSpellInfo().HasAttribute(SpellAttr7.DispelCharges);
                    // Cast duration to unsigned to prevent permanent aura's such as Righteous Fury being permanently added to caster
                    uint dur = (uint)Math.Min(2u * Time.Minute * Time.InMilliseconds, aura.GetDuration());

                    Aura oldAura = stealer.GetAura(aura.GetId(), aura.GetCasterGUID());
                    if (oldAura != null)
                    {
                        if (stealCharge)
                            oldAura.ModCharges(1);
                        else
                            oldAura.ModStackAmount(1);
                        oldAura.SetDuration((int)dur);
                    }
                    else
                    {
                        // single target state must be removed before aura creation to preserve existing single target aura
                        if (aura.IsSingleTarget())
                            aura.UnregisterSingleTarget();

                        Aura newAura = Aura.TryRefreshStackOrCreate(aura.GetSpellInfo(), aura.GetCastGUID(), effMask, stealer, null, baseDamage, null, aura.GetCasterGUID());
                        if (newAura != null)
                        {
                            // created aura must not be single target aura,, so stealer won't loose it on recast
                            if (newAura.IsSingleTarget())
                            {
                                newAura.UnregisterSingleTarget();
                                // bring back single target aura status to the old aura
                                aura.SetIsSingleTarget(true);
                                caster.GetSingleCastAuras().Add(aura);
                            }
                            // FIXME: using aura.GetMaxDuration() maybe not blizzlike but it fixes stealing of spells like Innervate
                            newAura.SetLoadedState(aura.GetMaxDuration(), (int)dur, stealCharge ? 1 : aura.GetCharges(), 1, recalculateMask, damage);
                            newAura.ApplyForTargets();
                        }
                    }

                    if (stealCharge)
                        aura.ModCharges(-1, AuraRemoveMode.EnemySpell);
                    else
                        aura.ModStackAmount(-1, AuraRemoveMode.EnemySpell);

                    return;
                }
            }
        }
        public void RemoveAurasDueToItemSpell(uint spellId, ObjectGuid castItemGuid)
        {
            foreach (var app in m_appliedAuras.LookupByKey(spellId))
            {
                if (app.GetBase().GetCastItemGUID() == castItemGuid)
                {
                    RemoveAura(app);
                }
            }
        }
        public void RemoveAurasByType(AuraType auraType, ObjectGuid casterGUID = default(ObjectGuid), Aura except = null, bool negative = true, bool positive = true)
        {
            var list = m_modAuras[auraType];
            for (var i = 0; i < list.Count; i++)
            {
                Aura aura = list[i].GetBase();
                AuraApplication aurApp = aura.GetApplicationOfTarget(GetGUID());

                if (aura != except && (casterGUID.IsEmpty() || aura.GetCasterGUID() == casterGUID)
                    && ((negative && !aurApp.IsPositive()) || (positive && aurApp.IsPositive())))
                {
                    uint removedAuras = m_removedAurasCount;
                    RemoveAura(aurApp);
                    if (m_removedAurasCount > removedAuras + 1)
                        i = 0;
                }
            }
        }
        public void RemoveNotOwnSingleTargetAuras(uint newPhase = 0, bool phaseid = false)
        {
            // Iterate m_ownedAuras - aura is marked as single target in Unit::AddAura (and pushed to m_ownedAuras).
            // m_appliedAuras will NOT contain the aura before first Unit::Update after adding it to m_ownedAuras.
            // Quickly removing such an aura will lead to it not being unregistered from caster's single cast auras container
            // leading to assertion failures if the aura was cast on a player that can
            // (and is changing map at the point where this function is called).
            // Such situation occurs when player is logging in inside an instance and fails the entry check for any reason.
            // The aura that was loaded from db (indirectly, via linked casts) gets removed before it has a chance
            // to register in m_appliedAuras
            var list = GetOwnedAuras().ToList();
            for (var i = 0; i < list.Count; i++)
            {
                Aura aura = list[i].Value;

                if (aura.GetCasterGUID() != GetGUID() && aura.IsSingleTarget())
                {
                    if (newPhase == 0 && !phaseid)
                        RemoveOwnedAura(list[i]);
                    else
                    {
                        Unit caster = aura.GetCaster();
                        if (!caster || (newPhase != 0 && !caster.IsInPhase(newPhase)) || (newPhase == 0 && !caster.IsInPhase(this)))
                            RemoveOwnedAura(list[i]);
                    }
                }
            }

            // single target auras at other targets
            for (var i = 0; i < m_scAuras.Count; i++)
            {
                var aura = m_scAuras[i];
                if (aura.GetUnitOwner() != this && !aura.GetUnitOwner().IsInPhase(newPhase))
                    aura.Remove();
            }
        }
        // All aura base removes should go threw this function!
        public void RemoveOwnedAura(KeyValuePair<uint, Aura> pair, AuraRemoveMode removeMode = AuraRemoveMode.Default)
        {
            Aura aura = pair.Value;
            Contract.Assert(!aura.IsRemoved());

            m_ownedAuras.Remove(pair);
            m_removedAuras.Add(aura);

            // Unregister single target aura
            if (aura.IsSingleTarget())
                aura.UnregisterSingleTarget();

            aura._Remove(removeMode);
        }
        public void RemoveOwnedAura(uint spellId, ObjectGuid casterGUID = default(ObjectGuid), uint reqEffMask = 0, AuraRemoveMode removeMode = AuraRemoveMode.Default)
        {
            var list = m_ownedAuras.Where(p => p.Key == spellId);
            foreach (var pair in list.ToList())
            {
                if (((pair.Value.GetEffectMask() & reqEffMask) == reqEffMask) && (casterGUID.IsEmpty() || pair.Value.GetCasterGUID() == casterGUID))
                    RemoveOwnedAura(pair, removeMode);
            }
        }
        public void RemoveOwnedAura(Aura aura, AuraRemoveMode removeMode = AuraRemoveMode.Default)
        {
            if (aura.IsRemoved())
                return;

            Contract.Assert(aura.GetOwner() == this);

            if (removeMode == AuraRemoveMode.None)
            {
                Log.outError(LogFilter.Spells, "Unit.RemoveOwnedAura() called with unallowed removeMode AURA_REMOVE_NONE, spellId {0}", aura.GetId());
                return;
            }

            uint spellId = aura.GetId();

            var range = m_ownedAuras.Where(p => p.Key == spellId);
            foreach (var pair in range.ToList())
            {
                if (pair.Value == aura)
                {
                    RemoveOwnedAura(pair, removeMode);
                    return;
                }
            }

            Contract.Assert(false);
        }

        public void RemoveAurasDueToSpell(uint spellId, ObjectGuid casterGUID = default(ObjectGuid), uint reqEffMask = 0, AuraRemoveMode removeMode = AuraRemoveMode.Default)
        {
            var list = m_appliedAuras.LookupByKey(spellId);
            if (list.Empty())
                return;

            foreach (var spell in list.ToList())
            {
                Aura aura = spell.GetBase();
                if (((aura.GetEffectMask() & reqEffMask) == reqEffMask)
                    && (casterGUID.IsEmpty() || aura.GetCasterGUID() == casterGUID))
                {
                    RemoveAura(spell, removeMode);
                }
            }
        }
        public void RemoveAurasDueToSpellByDispel(uint spellId, uint dispellerSpellId, ObjectGuid casterGUID, Unit dispeller, byte chargesRemoved = 1)
        {
            var range = m_ownedAuras.LookupByKey(spellId);
            foreach (var aura in range)
            {
                if (aura.GetCasterGUID() == casterGUID)
                {
                    DispelInfo dispelInfo = new DispelInfo(dispeller, dispellerSpellId, chargesRemoved);

                    // Call OnDispel hook on AuraScript
                    aura.CallScriptDispel(dispelInfo);

                    if (aura.GetSpellInfo().HasAttribute(SpellAttr7.DispelCharges))
                        aura.ModCharges(-dispelInfo.GetRemovedCharges(), AuraRemoveMode.EnemySpell);
                    else
                        aura.ModStackAmount(-dispelInfo.GetRemovedCharges(), AuraRemoveMode.EnemySpell);

                    // Call AfterDispel hook on AuraScript
                    aura.CallScriptAfterDispel(dispelInfo);

                    return;
                }
            }
        }
        public void RemoveAuraFromStack(uint spellId, ObjectGuid casterGUID = default(ObjectGuid), AuraRemoveMode removeMode = AuraRemoveMode.Default, ushort num = 1)
        {
            var range = m_ownedAuras.LookupByKey(spellId);
            foreach (var aura in range)
            {
                if ((aura.GetAuraType() == AuraObjectType.Unit) && (casterGUID.IsEmpty() || aura.GetCasterGUID() == casterGUID))
                {
                    aura.ModStackAmount(-num, removeMode);
                    return;
                }
            }
        }
        public void RemoveAura(KeyValuePair<uint, AuraApplication> appMap, AuraRemoveMode mode = AuraRemoveMode.Default)
        {
            var aurApp = appMap.Value;
            // Do not remove aura which is already being removed
            if (aurApp.HasRemoveMode())
                return;
            Aura aura = aurApp.GetBase();
            _UnapplyAura(appMap, mode);
            // Remove aura - for Area and Target auras
            if (aura.GetOwner() == this)
                aura.Remove(mode);
        }
        public void RemoveAura(uint spellId, ObjectGuid caster = default(ObjectGuid), uint reqEffMask = 0, AuraRemoveMode removeMode = AuraRemoveMode.Default)
        {
            var range = m_appliedAuras.LookupByKey(spellId);
            foreach (var iter in range)
            {
                Aura aura = iter.GetBase();
                if (((aura.GetEffectMask() & reqEffMask) == reqEffMask) && (caster.IsEmpty() || aura.GetCasterGUID() == caster))
                {
                    RemoveAura(iter, removeMode);
                    return;
                }
            }
        }
        public void RemoveAura(AuraApplication aurApp, AuraRemoveMode mode = AuraRemoveMode.Default)
        {
            // we've special situation here, RemoveAura called while during aura removal
            // this kind of call is needed only when aura effect removal handler
            // or event triggered by it expects to remove
            // not yet removed effects of an aura
            if (aurApp.HasRemoveMode())
            {
                // remove remaining effects of an aura
                for (byte effectIndex = 0; effectIndex < SpellConst.MaxEffects; ++effectIndex)
                {
                    if (aurApp.HasEffect(effectIndex))
                        aurApp._HandleEffect(effectIndex, false);
                }
                return;
            }
            // no need to remove
            if (aurApp.GetBase().GetApplicationOfTarget(GetGUID()) != aurApp || aurApp.GetBase().IsRemoved())
                return;

            uint spellId = aurApp.GetBase().GetId();

            var range = m_appliedAuras.Where(p => p.Key == spellId);
            foreach (var pair in range.ToList())
            {
                if (aurApp == pair.Value)
                {
                    RemoveAura(pair, mode);
                    return;
                }
            }
        }
        public void RemoveAura(Aura aura, AuraRemoveMode mode = AuraRemoveMode.Default)
        {
            if (aura.IsRemoved())
                return;
            AuraApplication aurApp = aura.GetApplicationOfTarget(GetGUID());
            if (aurApp != null)
                RemoveAura(aurApp, mode);
        }
        public void RemoveAurasWithAttribute(SpellAttr0 flags)
        {
            foreach (var app in GetAppliedAuras())
            {
                SpellInfo spell = app.Value.GetBase().GetSpellInfo();
                if (spell.HasAttribute(flags))
                    RemoveAura(app);
            }
        }
        public void RemoveAurasWithFamily(SpellFamilyNames family, FlagArray128 familyFlag, ObjectGuid casterGUID)
        {
            foreach (var pair in GetAppliedAuras())
            {
                Aura aura = pair.Value.GetBase();
                if (casterGUID.IsEmpty() || aura.GetCasterGUID() == casterGUID)
                {
                    SpellInfo spell = aura.GetSpellInfo();
                    if (spell.SpellFamilyName == family && spell.SpellFamilyFlags & familyFlag)
                    {
                        RemoveAura(pair);
                        continue;
                    }
                }
            }
        }

        public void RemoveAppliedAuras(Func<AuraApplication, bool> check)
        {
            foreach (var pair in m_appliedAuras)
            {
                if (check(pair.Value))
                    RemoveAura(pair);
            }
        }

        public void RemoveOwnedAuras(Func<Aura, bool> check)
        {
            foreach (var pair in m_ownedAuras)
            {
                if (check(pair.Value))
                    RemoveOwnedAura(pair);
            }
        }

        void RemoveAppliedAuras(uint spellId, Func<AuraApplication, bool> check)
        {
            foreach (var app in m_appliedAuras.LookupByKey(spellId))
            {
                if (check(app))
                    RemoveAura(app);
            }
        }

        void RemoveOwnedAuras(uint spellId, Func<Aura, bool> check)
        {
            foreach (var aura in m_ownedAuras.LookupByKey(spellId))
            {
                if (check(aura))
                    RemoveOwnedAura(aura);
            }
        }

        public void RemoveAurasByType(AuraType auraType, Func<AuraApplication, bool> check)
        {
            var list = m_modAuras[auraType];
            for (var i = 0; i < list.Count; ++i)
            {
                Aura aura = m_modAuras[auraType][i].GetBase();
                AuraApplication aurApp = aura.GetApplicationOfTarget(GetGUID());
                Contract.Assert(aurApp != null);

                if (check(aurApp))
                {
                    uint removedAuras = m_removedAurasCount;
                    RemoveAura(aurApp);
                    if (m_removedAurasCount > removedAuras + 1)
                        i = 0;
                }
            }
        }

        void RemoveAreaAurasDueToLeaveWorld()
        {
            // make sure that all area auras not applied on self are removed
            foreach (var pair in GetOwnedAuras())
            {
                var appMap = pair.Value.GetApplicationMap();
                foreach (var aurApp in appMap.Values.ToList())
                {
                    Unit target = aurApp.GetTarget();
                    if (target == this)
                        continue;
                    target.RemoveAura(aurApp);
                    // things linked on aura remove may apply new area aura - so start from the beginning
                }
            }

            // remove area auras owned by others
            foreach (var pair in GetAppliedAuras())
            {
                if (pair.Value.GetBase().GetOwner() != this)
                    RemoveAura(pair);
            }
        }
        public void RemoveAllAuras()
        {
            // this may be a dead loop if some events on aura remove will continiously apply aura on remove
            // we want to have all auras removed, so use your brain when linking events
            while (!m_appliedAuras.Empty())
                _UnapplyAura(m_appliedAuras.FirstOrDefault(), AuraRemoveMode.Default);

            while (!m_ownedAuras.Empty())
                RemoveOwnedAura(m_ownedAuras.FirstOrDefault());
        }
        public void RemoveArenaAuras()
        {
            // in join, remove positive buffs, on end, remove negative
            // used to remove positive visible auras in arenas
            RemoveAppliedAuras(aurApp =>
            {
                Aura aura = aurApp.GetBase();
                return !aura.GetSpellInfo().HasAttribute(SpellAttr4.Unk21) // don't remove stances, shadowform, pally/hunter auras
                    && !aura.IsPassive()                               // don't remove passive auras
                    && (aurApp.IsPositive() || !aura.GetSpellInfo().HasAttribute(SpellAttr3.DeathPersistent)); // not negative death persistent auras
            });
        }
        public void RemoveAllAurasExceptType(AuraType type)
        {
            foreach (var pair in GetAppliedAuras())
            {
                if (pair.Value == null)
                    continue;
                Aura aura = pair.Value.GetBase();
                if (!aura.GetSpellInfo().HasAura(GetMap().GetDifficultyID(), type))
                    _UnapplyAura(pair, AuraRemoveMode.Default);
            }

            foreach (var pair in GetOwnedAuras())
            {
                if (pair.Value == null)
                    continue;
                Aura aura = pair.Value;
                if (!aura.GetSpellInfo().HasAura(GetMap().GetDifficultyID(), type))
                    RemoveOwnedAura(pair, AuraRemoveMode.Default);
            }
        }
        public void RemoveAllAurasExceptType(AuraType type1, AuraType type2)
        {
            foreach (var pair in GetAppliedAuras())
            {
                Aura aura = pair.Value.GetBase();
                if (!aura.GetSpellInfo().HasAura(GetMap().GetDifficultyID(), type1) || !aura.GetSpellInfo().HasAura(GetMap().GetDifficultyID(), type2))
                    _UnapplyAura(pair, AuraRemoveMode.Default);
            }

            foreach (var pair in GetOwnedAuras())
            {
                Aura aura = pair.Value;
                if (!aura.GetSpellInfo().HasAura(GetMap().GetDifficultyID(), type1) || !aura.GetSpellInfo().HasAura(GetMap().GetDifficultyID(), type2))
                    RemoveOwnedAura(pair, AuraRemoveMode.Default);
            }
        }

        public void ModifyAuraState(AuraStateType flag, bool apply)
        {
            if (apply)
            {
                if (!HasFlag(UnitFields.AuraState, (1u << ((int)flag - 1))))
                {
                    SetFlag(UnitFields.AuraState, (1u << (int)flag - 1));
                    if (IsTypeId(TypeId.Player))
                    {
                        var sp_list = ToPlayer().GetSpellMap();
                        foreach (var spell in sp_list)
                        {
                            if (spell.Value.State == PlayerSpellState.Removed || spell.Value.Disabled)
                                continue;

                            SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(spell.Key);
                            if (spellInfo == null || !spellInfo.IsPassive())
                                continue;

                            if (spellInfo.CasterAuraState == flag)
                                CastSpell(this, spell.Key, true, null);
                        }
                    }
                    else if (IsPet())
                    {
                        Pet pet = ToPet();
                        foreach (var spell in pet.m_spells)
                        {
                            if (spell.Value.state == PetSpellState.Removed)
                                continue;
                            SpellInfo spellInfo = Global.SpellMgr.GetSpellInfo(spell.Key);
                            if (spellInfo == null || !spellInfo.IsPassive())
                                continue;
                            if (spellInfo.CasterAuraState == flag)
                                CastSpell(this, spell.Key, true, null);
                        }
                    }
                }
            }
            else
            {
                if (HasFlag(UnitFields.AuraState, (1u << (int)flag - 1)))
                {
                    RemoveFlag(UnitFields.AuraState, (1u << (int)flag - 1));

                    foreach (var app in GetAppliedAuras())
                    {
                        if (app.Value == null)
                            continue;

                        SpellInfo spellProto = app.Value.GetBase().GetSpellInfo();
                        if (app.Value.GetBase().GetCasterGUID() == GetGUID() && spellProto.CasterAuraState == flag && (spellProto.IsPassive() || flag != AuraStateType.Enrage))
                            RemoveAura(app);
                    }
                }
            }
        }
        public bool HasAuraState(AuraStateType flag, SpellInfo spellProto = null, Unit Caster = null)
        {
            if (Caster != null)
            {
                if (spellProto != null)
                {
                    var stateAuras = Caster.GetAuraEffectsByType(AuraType.AbilityIgnoreAurastate);
                    foreach (var aura in stateAuras)
                        if (aura.IsAffectingSpell(spellProto))
                            return true;
                }
                // Check per caster aura state
                // If aura with aurastate by caster not found return false
                if (Convert.ToBoolean((1 << (int)flag) & (int)AuraStateType.PerCasterAuraStateMask))
                {
                    var range = m_auraStateAuras.LookupByKey(flag);
                    foreach (var auraApp in range)
                        if (auraApp.GetBase().GetCasterGUID() == Caster.GetGUID())
                            return true;
                    return false;
                }
            }

            return HasFlag(UnitFields.AuraState, (uint)(1 << (int)flag - 1));
        }

        SpellSchools GetSpellSchoolByAuraGroup(UnitMods unitMod)
        {
            SpellSchools school = SpellSchools.Normal;

            switch (unitMod)
            {
                case UnitMods.ResistanceHoly:
                    school = SpellSchools.Holy;
                    break;
                case UnitMods.ResistanceFire:
                    school = SpellSchools.Fire;
                    break;
                case UnitMods.ResistanceNature:
                    school = SpellSchools.Nature;
                    break;
                case UnitMods.ResistanceFrost:
                    school = SpellSchools.Frost;
                    break;
                case UnitMods.ResistanceShadow:
                    school = SpellSchools.Shadow;
                    break;
                case UnitMods.ResistanceArcane:
                    school = SpellSchools.Arcane;
                    break;
            }

            return school;
        }

        public void _ApplyAllAuraStatMods()
        {
            foreach (var i in GetAppliedAuras())
                i.Value.GetBase().HandleAllEffects(i.Value, AuraEffectHandleModes.Stat, true);
        }
        public void _RemoveAllAuraStatMods()
        {
            foreach (var i in GetAppliedAuras())
                i.Value.GetBase().HandleAllEffects(i.Value, AuraEffectHandleModes.Stat, false);
        }

        // removes aura application from lists and unapplies effects
        public void _UnapplyAura(KeyValuePair<uint, AuraApplication> pair, AuraRemoveMode removeMode)
        {
            AuraApplication aurApp = pair.Value;
            Contract.Assert(aurApp != null);
            Contract.Assert(!aurApp.HasRemoveMode());
            Contract.Assert(aurApp.GetTarget() == this);

            aurApp.SetRemoveMode(removeMode);
            Aura aura = aurApp.GetBase();
            Log.outDebug(LogFilter.Spells, "Aura {0} now is remove mode {1}", aura.GetId(), removeMode);

            // dead loop is killing the server probably
            Contract.Assert(m_removedAurasCount < 0xFFFFFFFF);

            ++m_removedAurasCount;

            Unit caster = aura.GetCaster();

            m_appliedAuras.Remove(pair);

            if (aura.GetSpellInfo().AuraInterruptFlags != 0)
            {
                m_interruptableAuras.Remove(aurApp);
                UpdateInterruptMask();
            }

            bool auraStateFound = false;
            AuraStateType auraState = aura.GetSpellInfo().GetAuraState(GetMap().GetDifficultyID());
            if (auraState != 0)
            {
                bool canBreak = false;
                // Get mask of all aurastates from remaining auras
                var list = m_auraStateAuras.LookupByKey(auraState);
                for (var i = 0; i < list.Count && !(auraStateFound && canBreak);)
                {
                    if (list[i] == aurApp)
                    {
                        m_auraStateAuras.Remove(auraState, list[i]);
                        list = m_auraStateAuras.LookupByKey(auraState);
                        i = 0;
                        canBreak = true;
                        continue;
                    }
                    auraStateFound = true;
                    ++i;
                }
            }

            aurApp._Remove();
            aura._UnapplyForTarget(this, caster, aurApp);

            // remove effects of the spell - needs to be done after removing aura from lists
            for (byte c = 0; c < SpellConst.MaxEffects; ++c)
            {
                if (aurApp.HasEffect(c))
                    aurApp._HandleEffect(c, false);
            }

            // all effect mustn't be applied
            Contract.Assert(aurApp.GetEffectMask() == 0);

            // Remove totem at next update if totem loses its aura
            if (aurApp.GetRemoveMode() == AuraRemoveMode.Expire && IsTypeId(TypeId.Unit) && IsTotem())
            {
                if (ToTotem().GetSpell() == aura.GetId() && ToTotem().GetTotemType() == TotemType.Passive)
                    ToTotem().setDeathState(DeathState.JustDied);
            }

            // Remove aurastates only if were not found
            if (!auraStateFound)
                ModifyAuraState(auraState, false);

            aura.HandleAuraSpecificMods(aurApp, caster, false, false);
        }

        public void _UnapplyAura(AuraApplication aurApp, AuraRemoveMode removeMode)
        {
            // aura can be removed from unit only if it's applied on it, shouldn't happen
            Contract.Assert(aurApp.GetBase().GetApplicationOfTarget(GetGUID()) == aurApp);

            uint spellId = aurApp.GetBase().GetId();
            var range = m_appliedAuras.LookupByKey(spellId);

            foreach (var app in range)
            {
                if (app == aurApp)
                {
                    _UnapplyAura(new KeyValuePair<uint, AuraApplication>(spellId, app), removeMode);
                    return;
                }
            }
            Contract.Assert(false);
        }

        public AuraEffect GetAuraEffect(uint spellId, uint effIndex, ObjectGuid casterGUID = default(ObjectGuid))
        {
            var range = m_appliedAuras.LookupByKey(spellId);
            if (!range.Empty())
            {
                foreach (var aura in range)
                {
                    if (aura.HasEffect(effIndex)
                            && (casterGUID.IsEmpty() || aura.GetBase().GetCasterGUID() == casterGUID))
                    {
                        return aura.GetBase().GetEffect(effIndex);
                    }
                }
            }
            return null;
        }
        public AuraEffect GetAuraEffectOfRankedSpell(uint spellId, uint effIndex, ObjectGuid casterGUID = default(ObjectGuid))
        {
            uint rankSpell = Global.SpellMgr.GetFirstSpellInChain(spellId);
            while (rankSpell != 0)
            {
                AuraEffect aurEff = GetAuraEffect(rankSpell, effIndex, casterGUID);
                if (aurEff != null)
                    return aurEff;
                rankSpell = Global.SpellMgr.GetNextSpellInChain(rankSpell);
            }
            return null;
        }
        
        // spell mustn't have familyflags
        public AuraEffect GetAuraEffect(AuraType type, SpellFamilyNames family, FlagArray128 familyFlag, ObjectGuid casterGUID = default(ObjectGuid))
        {
            var auras = GetAuraEffectsByType(type);
            foreach (var aura in auras)
            {
                SpellInfo spell = aura.GetSpellInfo();
                if (spell.SpellFamilyName == family && spell.SpellFamilyFlags & familyFlag)
                {
                    if (!casterGUID.IsEmpty() && aura.GetCasterGUID() != casterGUID)
                        continue;
                    return aura;
                }
            }
            return null;

        }

        public AuraApplication GetAuraApplication(uint spellId, ObjectGuid casterGUID = default(ObjectGuid), ObjectGuid itemCasterGUID = default(ObjectGuid), uint reqEffMask = 0, AuraApplication except = null)
        {
            var range = m_appliedAuras.LookupByKey(spellId);
            if (!range.Empty())
            {
                foreach (var app in range)
                {
                    Aura aura = app.GetBase();
                    if (((aura.GetEffectMask() & reqEffMask) == reqEffMask) && (casterGUID.IsEmpty() || aura.GetCasterGUID() == casterGUID)
                        && (itemCasterGUID.IsEmpty() || aura.GetCastItemGUID() == itemCasterGUID) && (except == null || except != app))
                    {
                        return app;
                    }
                }
            }
            return null;
        }
        public Aura GetAura(uint spellId, ObjectGuid casterGUID = default(ObjectGuid), ObjectGuid itemCasterGUID = default(ObjectGuid), uint reqEffMask = 0)
        {
            AuraApplication aurApp = GetAuraApplication(spellId, casterGUID, itemCasterGUID, reqEffMask);
            return aurApp?.GetBase();
        }

        uint BuildAuraStateUpdateForTarget(Unit target)
        {
            uint auraStates = GetUInt32Value(UnitFields.AuraState) & ~(uint)AuraStateType.PerCasterAuraStateMask;
            foreach (var state in m_auraStateAuras)
                if (Convert.ToBoolean((1 << (int)state.Key - 1) & (uint)AuraStateType.PerCasterAuraStateMask))
                    if (state.Value.GetBase().GetCasterGUID() == target.GetGUID())
                        auraStates |= (uint)(1 << (int)state.Key - 1);

            return auraStates;
        }

        public bool CanProc() { return m_procDeep == 0; }

        public void _ApplyAuraEffect(Aura aura, uint effIndex)
        {
            Contract.Assert(aura != null);
            Contract.Assert(aura.HasEffect(effIndex));
            AuraApplication aurApp = aura.GetApplicationOfTarget(GetGUID());
            Contract.Assert(aurApp != null);
            if (aurApp.GetEffectMask() == 0)
                _ApplyAura(aurApp, (uint)(1 << (int)effIndex));
            else
                aurApp._HandleEffect(effIndex, true);
        }
        // handles effects of aura application
        // should be done after registering aura in lists
        public void _ApplyAura(AuraApplication aurApp, uint effMask)
        {
            Aura aura = aurApp.GetBase();

            _RemoveNoStackAurasDueToAura(aura);

            if (aurApp.HasRemoveMode())
                return;

            // Update target aura state flag
            AuraStateType aState = aura.GetSpellInfo().GetAuraState(GetMap().GetDifficultyID());
            if (aState != 0)
                ModifyAuraState(aState, true);

            if (aurApp.HasRemoveMode())
                return;

            // Sitdown on apply aura req seated
            if (aura.GetSpellInfo().AuraInterruptFlags.HasAnyFlag(SpellAuraInterruptFlags.NotSeated) && !IsSitState())
                SetStandState(UnitStandStateType.Sit);

            Unit caster = aura.GetCaster();

            if (aurApp.HasRemoveMode())
                return;

            aura.HandleAuraSpecificMods(aurApp, caster, true, false);
            aura.HandleAuraSpecificPeriodics(aurApp, caster);

            // apply effects of the aura
            for (byte i = 0; i < SpellConst.MaxEffects; i++)
            {
                if (Convert.ToBoolean(effMask & 1 << i) && !(aurApp.HasRemoveMode()))
                    aurApp._HandleEffect(i, true);
            }
        }
        public void _AddAura(UnitAura aura, Unit caster)
        {
            Contract.Assert(!m_cleanupDone);
            m_ownedAuras.Add(aura.GetId(), aura);

            _RemoveNoStackAurasDueToAura(aura);

            if (aura.IsRemoved())
                return;

            aura.SetIsSingleTarget(caster != null && aura.GetSpellInfo().IsSingleTarget() || aura.HasEffectType(AuraType.ControlVehicle));
            if (aura.IsSingleTarget())
            {

                // @HACK: Player is not in world during loading auras.
                //Single target auras are not saved or loaded from database
                //but may be created as a result of aura links (player mounts with passengers)
                Contract.Assert((IsInWorld && !IsDuringRemoveFromWorld()) || (aura.GetCasterGUID() == GetGUID()) || (IsLoading() && aura.HasEffectType(AuraType.ControlVehicle)));

                // register single target aura
                caster.m_scAuras.Add(aura);
                // remove other single target auras
                var scAuras = caster.GetSingleCastAuras();
                for (var i = 0; i < scAuras.Count; ++i)
                {
                    var aur = scAuras[i];
                    if (aur != aura && aur.IsSingleTargetWith(aura))
                        aur.Remove();

                }
            }
        }
        public Aura _TryStackingOrRefreshingExistingAura(SpellInfo newAura, uint effMask, Unit caster, int[] baseAmount = null, Item castItem = null, ObjectGuid casterGUID = default(ObjectGuid), int castItemLevel = -1)
        {
            Contract.Assert(!casterGUID.IsEmpty() || caster);

            // Check if these can stack anyway
            if (casterGUID.IsEmpty() && !newAura.IsStackableOnOneSlotWithDifferentCasters())
                casterGUID = caster.GetGUID();

            // passive and Incanter's Absorption and auras with different type can stack with themselves any number of times
            if (!newAura.IsMultiSlotAura())
            {
                // check if cast item changed
                ObjectGuid castItemGUID = ObjectGuid.Empty;
                if (castItem != null)
                {
                    castItemGUID = castItem.GetGUID();
                    castItemLevel = (int)castItem.GetItemLevel(castItem.GetOwner());
                }

                // find current aura from spell and change it's stackamount, or refresh it's duration
                Aura foundAura = GetOwnedAura(newAura.Id, casterGUID, (newAura.HasAttribute(SpellCustomAttributes.EnchantProc) ? castItemGUID : ObjectGuid.Empty), 0);
                if (foundAura != null)
                {
                    // effect masks do not match
                    // extremely rare case
                    // let's just recreate aura
                    if (effMask != foundAura.GetEffectMask())
                        return null;

                    // update basepoints with new values - effect amount will be recalculated in ModStackAmount
                    foreach (SpellEffectInfo effect in foundAura.GetSpellEffectInfos())
                    {
                        if (effect == null)
                            continue;

                        AuraEffect eff = foundAura.GetEffect(effect.EffectIndex);
                        if (eff == null)
                            continue;

                        int bp;
                        if (baseAmount != null)
                            bp = baseAmount[effect.EffectIndex];
                        else
                            bp = effect.BasePoints;

                        int oldBP = eff.m_baseAmount;
                        oldBP = bp;
                    }

                    // correct cast item guid if needed
                    if (castItemGUID != foundAura.GetCastItemGUID())
                    {
                        castItemGUID = foundAura.GetCasterGUID();
                        castItemLevel = foundAura.GetCastItemLevel();
                    }

                    // try to increase stack amount
                    foundAura.ModStackAmount(1);
                    return foundAura;
                }
            }

            return null;
        }

        void _RemoveNoStackAurasDueToAura(Aura aura)
        {
            SpellInfo spellProto = aura.GetSpellInfo();

            // passive spell special case (only non stackable with ranks)
            if (spellProto.IsPassiveStackableWithRanks())
                return;

            if (!IsHighestExclusiveAura(aura))
            {
                if (!aura.GetSpellInfo().IsAffectingArea(GetMap().GetDifficultyID()))
                {
                    Unit caster = aura.GetCaster();
                    if (caster && caster.IsTypeId(TypeId.Player))
                        Spell.SendCastResult(caster.ToPlayer(), aura.GetSpellInfo(), aura.GetSpellXSpellVisualId(), aura.GetCastGUID(), SpellCastResult.AuraBounced);
                }

                aura.Remove();
                return;
            }

            bool remove = false;
            for (var i = 0; i < m_appliedAuras.KeyValueList.Count; i++)
            {
                var app = m_appliedAuras.KeyValueList[i];
                if (remove)
                {
                    remove = false;
                    i = 0;
                }

                if (aura.CanStackWith(app.Value.GetBase()))
                    continue;

                RemoveAura(app, AuraRemoveMode.Default);
                if (i == m_appliedAuras.KeyValueList.Count - 1)
                    break;
                remove = true;
            }
        }
        public int GetHighestExclusiveSameEffectSpellGroupValue(AuraEffect aurEff, AuraType auraType, bool checkMiscValue = false, int miscValue = 0)
        {
            int val = 0;
            var spellGroupList = Global.SpellMgr.GetSpellSpellGroupMapBounds(aurEff.GetSpellInfo().GetFirstRankSpell().Id);
            foreach (var spellGroup in spellGroupList)
            {
                if (Global.SpellMgr.GetSpellGroupStackRule(spellGroup) == SpellGroupStackRule.ExclusiveSameEffect)
                {
                    var auraEffList = GetAuraEffectsByType(auraType);
                    foreach (var auraEffect in auraEffList)
                    {
                        if (aurEff != auraEffect && (!checkMiscValue || auraEffect.GetMiscValue() == miscValue) &&
                            Global.SpellMgr.IsSpellMemberOfSpellGroup(auraEffect.GetSpellInfo().Id, spellGroup))
                        {
                            // absolute value only
                            if (Math.Abs(val) < Math.Abs(auraEffect.GetAmount()))
                                val = auraEffect.GetAmount();
                        }
                    }
                }
            }
            return val;
        }

        void UpdateLastDamagedTime(SpellInfo spellProto)
        {
            if (!IsTypeId(TypeId.Unit) || IsPet())
                return;

            if (spellProto != null && spellProto.HasAura(Difficulty.None, AuraType.DamageShield))
                return;

            SetLastDamagedTime(Time.UnixTime);
        }

        public bool IsHighestExclusiveAura(Aura aura, bool removeOtherAuraApplications = false)
        {
            foreach (AuraEffect aurEff in aura.GetAuraEffects())
            {
                if (aurEff == null)
                    continue;

                AuraType auraType = aurEff.GetSpellEffectInfo().ApplyAuraName;
                var auras = GetAuraEffectsByType(auraType);
                for (var i = 0; i < auras.Count;)
                {
                    AuraEffect existingAurEff = auras[i];
                    ++i;

                    if (Global.SpellMgr.CheckSpellGroupStackRules(aura.GetSpellInfo(), existingAurEff.GetSpellInfo()) == SpellGroupStackRule.ExclusiveHighest)
                    {
                        int diff = Math.Abs(aurEff.GetAmount()) - Math.Abs(existingAurEff.GetAmount());
                        if (diff == 0)
                            diff = (int)(aura.GetEffectMask() - existingAurEff.GetBase().GetEffectMask());

                        if (diff > 0)
                        {
                            Aura auraBase = existingAurEff.GetBase();
                            // no removing of area auras from the original owner, as that completely cancels them
                            if (removeOtherAuraApplications && (!auraBase.IsArea() || auraBase.GetOwner() != this))
                            {
                                AuraApplication aurApp = existingAurEff.GetBase().GetApplicationOfTarget(GetGUID());
                                if (aurApp != null)
                                {
                                    bool hasMoreThanOneEffect = auraBase.HasMoreThanOneEffectForType(auraType);
                                    uint removedAuras = m_removedAurasCount;
                                    RemoveAura(aurApp);
                                    if (hasMoreThanOneEffect || m_removedAurasCount > removedAuras + 1)
                                        i = 0;
                                }
                            }
                        }
                        else if (diff < 0)
                            return false;
                    }
                }

            }

            return true;
        }

        public Aura GetOwnedAura(uint spellId, ObjectGuid casterGUID = default(ObjectGuid), ObjectGuid itemCasterGUID = default(ObjectGuid), uint reqEffMask = 0, Aura except = null)
        {
            var range = m_ownedAuras.LookupByKey(spellId);
            foreach (var aura in range)
            {
                if (((aura.GetEffectMask() & reqEffMask) == reqEffMask) && (casterGUID.IsEmpty() || aura.GetCasterGUID() == casterGUID)
                    && (itemCasterGUID.IsEmpty() || aura.GetCastItemGUID() == itemCasterGUID) && (except == null || except != aura))
                {
                    return aura;
                }
            }
            return null;
        }

        public List<AuraEffect> GetAuraEffectsByType(AuraType type)
        {
            return m_modAuras.LookupByKey(type);
        }
        public int GetMaxPositiveAuraModifierByMiscMask(AuraType auratype, uint miscMask, AuraEffect except = null)
        {
            int modifier = 0;

            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var eff in mTotalAuraList)
            {
                if (except != eff && Convert.ToBoolean(eff.GetMiscValue() & miscMask) && eff.GetAmount() > modifier)
                    modifier = eff.GetAmount();
            }

            return modifier;
        }
        public int GetMaxNegativeAuraModifierByMiscMask(AuraType auratype, uint miscMask)
        {
            int modifier = 0;

            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var eff in mTotalAuraList)
            {
                if (Convert.ToBoolean(eff.GetMiscValue() & miscMask) && eff.GetAmount() < modifier)
                    modifier = eff.GetAmount();
            }

            return modifier;
        }
        public int GetTotalAuraModifier(AuraType auratype)
        {
            Dictionary<SpellGroup, int> SameEffectSpellGroup = new Dictionary<SpellGroup, int>();
            int modifier = 0;

            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var aura in mTotalAuraList)
                if (!Global.SpellMgr.AddSameEffectStackRuleSpellGroups(aura.GetSpellInfo(), aura.GetAmount(), out SameEffectSpellGroup))
                    modifier += aura.GetAmount();

            foreach (var pair in SameEffectSpellGroup)
                modifier += pair.Value;

            return modifier;
        }
        public float GetTotalAuraMultiplier(AuraType auratype)
        {
            float multiplier = 1.0f;

            var mTotalAuraList = GetAuraEffectsByType(auratype);
            foreach (var aura in mTotalAuraList)
                MathFunctions.AddPct(ref multiplier, aura.GetAmount());

            return multiplier;
        }

        public void _RegisterAuraEffect(AuraEffect aurEff, bool apply)
        {
            if (apply)
                m_modAuras.Add(aurEff.GetAuraType(), aurEff);
            else
                m_modAuras.Remove(aurEff.GetAuraType(), aurEff);
        }
        public float GetTotalAuraModValue(UnitMods unitMod)
        {
            if (unitMod >= UnitMods.End)
            {
                Log.outError(LogFilter.Unit, "attempt to access non-existing UnitMods in GetTotalAuraModValue()!");
                return 0.0f;
            }

            if (m_auraModifiersGroup[(int)unitMod][(int)UnitModifierType.TotalPCT] <= 0.0f)
                return 0.0f;

            float value = MathFunctions.CalculatePct(m_auraModifiersGroup[(int)unitMod][(int)UnitModifierType.BaseValue], Math.Max(m_auraModifiersGroup[(int)unitMod][(int)UnitModifierType.BasePCTExcludeCreate], -100.0f));
            value *= m_auraModifiersGroup[(int)unitMod][(int)UnitModifierType.BasePCT];
            value += m_auraModifiersGroup[(int)unitMod][(int)UnitModifierType.TotalValue];
            value *= m_auraModifiersGroup[(int)unitMod][(int)UnitModifierType.TotalPCT];

            return value;
        }

        public void SetVisibleAura(AuraApplication aurApp)
        {
            m_visibleAuras.Add(aurApp);
            m_visibleAurasToUpdate.Add(aurApp);
            UpdateAuraForGroup();
        }

        public void RemoveVisibleAura(AuraApplication aurApp)
        {
            m_visibleAuras.Remove(aurApp);
            m_visibleAurasToUpdate.Remove(aurApp);
            UpdateAuraForGroup();
        }

        void UpdateAuraForGroup()
        {
            Player player = ToPlayer();
            if (player != null)
            {
                if (player.GetGroup() != null)
                    player.SetGroupUpdateFlag(GroupUpdateFlags.Auras);
            }
            else if (IsPet())
            {
                Pet pet = ToPet();
                if (pet.isControlled())
                    pet.SetGroupUpdateFlag(GroupUpdatePetFlags.Auras);
            }
        }

        public SortedSet<AuraApplication> GetVisibleAuras() { return m_visibleAuras; }
        public bool HasVisibleAura(AuraApplication aurApp) { return m_visibleAuras.Contains(aurApp); }
        public void SetVisibleAuraUpdate(AuraApplication aurApp) { m_visibleAurasToUpdate.Add(aurApp); }
    }
}
