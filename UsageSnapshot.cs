using System;
using System.Collections.Generic;

namespace CioStats
{
    // One usage limit "bucket" (e.g. 5-hour session, weekly) parsed from claude.ai.
    public sealed class UsageBucket
    {
        public string Label;
        public double Percent;       // 0..100
        public DateTime? ResetsAt;   // UTC
        public long? Used;
        public long? Limit;
    }

    // Snapshot of claude.ai subscription usage at a point in time.
    public sealed class UsageSnapshot
    {
        public bool SignedIn;
        public string Plan;
        public string SubscriberName;
        public string Note;
        public DateTime FetchedAtUtc;
        public List<UsageBucket> Buckets = new List<UsageBucket>();

        public UsageBucket Primary { get { return Buckets.Count > 0 ? Buckets[0] : null; } }
        public UsageBucket Secondary { get { return Buckets.Count > 1 ? Buckets[1] : null; } }

        public double MaxPercent
        {
            get
            {
                double m = 0;
                foreach (UsageBucket b in Buckets) if (b.Percent > m) m = b.Percent;
                return m;
            }
        }
    }
}
