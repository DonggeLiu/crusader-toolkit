using AutoTax;
int checks = 0;
void Check(bool ok, string label) { checks++; if (!ok) { Console.Error.WriteLine(label); Environment.Exit(1); } }
int Decide(TaxPolicy p, double popularity, int other, int tax, int tick = 100,
    int? snapshotTax = null,
    double lower = 99, double upper = 99.5)
{
    int effect = snapshotTax ?? TaxPolicy.TaxEffect(tax);
    return p.Evaluate(tick, (int)Math.Round(popularity * 100), other + effect, effect,
        tax, lower, upper);
}
Check(TaxPolicy.FoodEffect(2,true,1)==0,"Full rations, one food has no modifier");
Check(TaxPolicy.FoodEffect(2,true,4)==75,"Four foods contribute diversity");
Check(TaxPolicy.FoodEffect(4,true,2)==225,"Double rations plus diversity");
Check(TaxPolicy.FoodEffect(4,false,1)==-200,"Empty stocks override generous ration settings");
Check(TaxPolicy.FoodEffect(0,true,1)==-200,"No rations gives food penalty even with stock");
var p = new TaxPolicy();
Check(Decide(p,100,0,3)==4,"Cap immediately starts lowest profitable tax without a month delay");
Check(Decide(p,100,0,4)==4,"Repeated cap samples do not climb through all tax levels");
Check(Decide(p,99.5,0,4)==4,"Harvest above lower bound");
Check(Decide(p,99,0,4)==3,"No bonuses: zero tax restores popularity");
Check(Decide(p,99.25,0,3)==3,"Recovery holds until upper target");
Check(Decide(p,99.5,0,3)==4,"Upper target below 100 starts harvest");
Check(Decide(p,99,75,4)==4,"Food diversity bonus: recovery can still levy tax");
Check(p.PredictedChange==25,"Recovery predicts a strictly positive change");
Check(Decide(p,99.5,75,4)==5,"New bonus increases harvest tax");
Check(Decide(p,99.25,0,5)==3,"Bonus disappears: cut tax before predicted lower-bound crossing");
Check(Decide(p,99.25,100,3)==5 && p.CurrentPhase==TaxPolicy.Phase.Balanced,"Exact sustainable tax holds popularity");
Check(Decide(p,99,100,5)==4,"At lower bound use positive rather than flat trend");
Check(Decide(p,100,100,5)==6,"At cap re-evaluate immediately even from balanced tax");
p.Reset();
Check(Decide(p,99.5,325,3)==8,"Wide tax step: grow headroom rather than cross floor");
Check(Decide(p,99.75,325,8)==9,"Enough headroom permits the wide step");
Check(Decide(p,99,325,9)==8,"Return to positive tax after wide step");
p.Reset();
Check(Decide(p,99.6,25,3,upper:99.75)==4,"Predict a known cap overshoot before reaching target");
Check(Decide(p,99,-100,7)==3 && p.CannotRecover,"No legal tax can cure a food shortage without donations");
Check(Decide(p,99,-100,3)==3 && p.CannotRecover,"Even with severe shortage automation never subsidizes");
foreach (int manual in new[]{0,1,2}) {
    Check(Decide(p,100,0,manual)==manual && p.CurrentPhase==TaxPolicy.Phase.ManualDonation,"Manual donation survives cap handling");
    Check(Decide(p,40,-100,manual)==manual,"Manual donation survives low popularity");
}
Check(Decide(p,99.5,0,3)==4,"Return to no tax resumes automation");

Check(Decide(p,100,700,3)==11 && p.AtTaxLimit,"All taxes still positive: maximum tax and explicit limit");
Check(Decide(p,99,-300,3)==3 && p.CannotRecover,"Severe crisis must not spend treasury");
// The displayed tax contribution may be old or a UI preview. It must cancel.
foreach(int oldTax in Enumerable.Range(0,12)) {
    p.Reset(); Check(Decide(p,99.5,75,6,snapshotTax:TaxPolicy.TaxEffect(oldTax))==5,"Snapshot tax cancels independently of live tax");
}
p.Reset(); Decide(p,99.5,0,3,tick:1000);
Check(Decide(p,99,75,4,tick:5)==4,"Time rollback resets phase but still reacts immediately");
Check(Decide(p,99.25,0,11)==3,"Manual change is re-evaluated from current economy");
Check(Decide(p,99.5,0,4,lower:99.5,upper:99.5)==4,"Invalid target band makes no write");
Check(!TaxPolicy.ValidBand(99,100) && !TaxPolicy.ValidBand(double.NaN,99.5),"Upper must be below cap and bounds finite");
// Fixed-economy closed loops: production code selects the tax, native table moves
// popularity; frequent UI polls between economic updates must not cause chatter.
foreach (int other in Enumerable.Range(0,25).Select(n=>n*25)) {
    p.Reset(); int pop=10000, tax=3;
    for(int update=0; update<400; update++) {
        int tick=update*200;
        int selected=p.Evaluate(tick,pop,other+TaxPolicy.TaxEffect(tax),TaxPolicy.TaxEffect(tax),tax,99,99.5);
        Check(selected>=3 && selected<=11,"Tax stays in allowed bounds");
        int again=p.Evaluate(tick+1,pop,other+TaxPolicy.TaxEffect(tax),TaxPolicy.TaxEffect(tax),selected,99,99.5);
        Check(again==selected,"Repeated old-snapshot poll is stable");
        int projected=pop+other+TaxPolicy.TaxEffect(selected);
        Check(projected>=9900 && projected<=10000,$"Known nonnegative bonus loop avoids floor breach and cap waste: bonus={other}, pop={pop}, tax={selected}, next={projected}, update={update}");
        pop=Math.Clamp(projected,0,10000); tax=selected;
    }
}
// Realistic changing food mix; bonuses can appear/disappear while recovering or collecting.
p.Reset(); int changingPop=10000, changingTax=3;
for(int update=0;update<300;update++) {
    int bonus = new[]{0,25,75,50,100,0}[update/10%6];
    int next=Decide(p,changingPop/100.0,bonus,changingTax,tick:update*200);
    changingPop += bonus+TaxPolicy.TaxEffect(next); changingTax=next;
    Check(changingPop>=9900 && changingPop<=10000,"Changing food bonuses stay within safe high band");
}
// Save round trips must be independent of the machine's language and defaults.
var defaults = new MatchOptions(false, 99, 99.5);
var local = MatchOptions.Read(null, defaults);
local.Enabled = true; local.Adjust(true, -1);
Check(!defaults.Enabled && defaults.Lower == 99, "Match edits never mutate defaults");
var culture = System.Globalization.CultureInfo.CurrentCulture;
try {
    System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
    var saved = local.Serialize();
    var restored = MatchOptions.Read(saved, new MatchOptions(false, 70, 80));
    Check(restored.Enabled && restored.Lower == 98.75 && restored.Upper == 99.5, "Save restores its own settings across defaults and locale");
    local.Baseline = 5; local.AddExtra(-12.5); local.AddExtra(340.25); local.CountAdjustment(); local.CountAdjustment();
    var tracked = MatchOptions.Read(local.Serialize(), defaults);
    Check(tracked.Extra == 327.75 && tracked.Baseline == 5 && tracked.Adjustments == 2, "Save restores extra earnings, baseline and adjustment count");
} finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
var older = MatchOptions.Read("2|1|98|99|500", defaults);
Check(older.Enabled && older.Lower == 98 && older.Extra == 0 && older.Baseline == -1 && older.Adjustments == 0,
    "Older saves keep settings; extra earnings start from zero");
foreach (var invalid in new[] { "", "3|1|99|99.5", "1|1|NaN|99.5", "1|1|99|100", "1|1|99.5|99", "1|x|99|99.5" }) {
    var restored = MatchOptions.Read(invalid, defaults);
    Check(!restored.Enabled && restored.Lower == 99 && restored.Upper == 99.5, "Missing or invalid save uses defaults");
}
foreach (bool isLower in new[] { true, false }) foreach (int direction in new[] { -1, 1 }) {
    var bounds = new MatchOptions(false, 99, 99.5);
    for (int i = 0; i < 500; i++) {
        bounds.Adjust(isLower, direction);
        Check(TaxPolicy.ValidBand(bounds.Lower, bounds.Upper), "Repeated UI clicks keep a valid band");
    }
}
Console.WriteLine($"PASS: {checks} dynamic policy checks");
