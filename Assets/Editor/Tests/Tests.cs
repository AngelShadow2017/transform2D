using System;
using System.Collections.Generic;
using Core.TrueSync;
using NUnit.Framework;
using Random = System.Random;

#if UNITY_2021_1_OR_NEWER
using UnityEngine;
#endif

// 假设工程已有 FP / MathBurstedFix / 相关运算与隐式转换；此处不再使用 Raw 版本。
// 若 FP 没有隐式 (double)->FP, 需改为项目内现有创建方式。

namespace TrueSyncWrappedTests
{
    [TestFixture]
    public class TrigConsistency_WrappedTests
    {
        const int SAMPLES_ASIN_ACOS = 40000;
        const int SAMPLES_ATAN_NARROW = 30000;
        const int SAMPLES_ATAN_WIDE = 30000;

        // 误差容差
        static readonly FP AbsTol = (FP)1e-6;      // 绝对误差
        static readonly FP AbsTolAtan = (FP)2e-6;  // atan 略放宽

        struct Stat
        {
            public FP maxAbs;
            public long overTol;
            public long count;
            public void Acc(FP expected, FP got, FP tol)
            {
                FP d = FP.Abs(got - expected);
                if (d > maxAbs) maxAbs = d;
                if (d > tol) overTol++;
                count++;
            }
            public override string ToString()
            {
                return $"Samples={count} MaxAbs={maxAbs} OverTol={overTol}";
            }
        }

        [Test]
        public void Test_Asin_Acos_Wrapped()
        {
            var rand = new Random(1234);
            var statAsin = new Stat();
            var statAcos = new Stat();

            // 边界 / 特殊
            List<FP> values = new List<FP>
            {
                (FP)0,
                (FP)1,
                (FP)(-1),
                (FP)0.5,
                (FP)(-0.5),
                (FP)(0.999999),
                (FP)(-0.999999)
            };

            // 随机 [-1,1]
            for (int i = 0; i < SAMPLES_ASIN_ACOS; i++)
            {
                double v = rand.NextDouble() * 2.0 - 1.0;
                values.Add((FP)v);
            }

            foreach (var x in values)
            {
                double xd = (double)x;
                FP asinExpected = (FP)Math.Asin(xd);
                FP acosExpected = (FP)Math.Acos(xd);

                FP asinGot = FP.Asin(x);
                FP acosGot = FP.Acos(x);

                statAsin.Acc(asinExpected, asinGot, AbsTol);
                statAcos.Acc(acosExpected, acosGot, AbsTol);
            }

            Log("ASIN", statAsin, AbsTol);
            Log("ACOS", statAcos, AbsTol);

            Assert.LessOrEqual((double)statAsin.maxAbs, (double)AbsTol * 4, "Asin 最大绝对误差超限");
            Assert.LessOrEqual((double)statAcos.maxAbs, (double)AbsTol * 4, "Acos 最大绝对误差超限");
        }

        [Test]
        public void Test_Atan_Wrapped()
        {
            var rand = new Random(5678);
            var statAtan = new Stat();
            List<FP> vals = new List<FP>
            {
                (FP)0,
                (FP)1,
                (FP)(-1),
                (FP)10,
                (FP)(-10),
                (FP)1000,
                (FP)(-1000)
            };

            // 中等范围密集 [-8,8]
            for (int i = 0; i < SAMPLES_ATAN_NARROW; i++)
            {
                double v = (rand.NextDouble() * 2 - 1) * 8.0;
                vals.Add((FP)v);
            }
            // 宽范围，测试 1/x 分支
            for (int i = 0; i < SAMPLES_ATAN_WIDE; i++)
            {
                double sign = rand.Next(2) == 0 ? 1 : -1;
                double mag = Math.Pow(10, rand.NextDouble() * 4); // 10^0 ~ 10^4
                vals.Add((FP)(sign * mag));
            }

            foreach (var v in vals)
            {
                double vd = (double)v;
                FP expected = (FP)Math.Atan(vd);
                FP got = FP.Atan(v);
                statAtan.Acc(expected, got, AbsTolAtan);
            }

            Log("ATAN", statAtan, AbsTolAtan);
            Assert.LessOrEqual((double)statAtan.maxAbs, (double)AbsTolAtan * 4, "Atan 最大绝对误差超限");
        }
        // Atan2 采样数量
        const int SAMPLES_ATAN2 = 60000;

        [Test]
        public void Test_Atan2_Wrapped()
        {
            var rand = new Random(91011);
            var stat = new Stat();

            var pairs = new List<(FP y, FP x)>
            {
                ((FP)0,(FP)0),
                ((FP)0,(FP)1),
                ((FP)0,(FP)(-1)),
                ((FP)1,(FP)0),
                ((FP)(-1),(FP)0),
                ((FP)1,(FP)1),
                ((FP)1,(FP)(-1)),
                ((FP)(-1),(FP)1),
                ((FP)(-1),(FP)(-1)),
                ((FP)123.0,(FP)0.5),
                ((FP)(-250.0),(FP)800.0),
            };

            // 中等范围随机 [-8,8]
            for (int i = 0; i < SAMPLES_ATAN2 / 2; i++)
            {
                double y = (rand.NextDouble() * 2 - 1) * 8.0;
                double x = (rand.NextDouble() * 2 - 1) * 8.0;
                pairs.Add(((FP)y, (FP)x));
            }

            // 宽范围，测试比例极端
            for (int i = 0; i < SAMPLES_ATAN2 / 2; i++)
            {
                double magY = Math.Pow(10, rand.NextDouble() * 4); // 10^0 ~ 10^4
                double magX = Math.Pow(10, rand.NextDouble() * 4);
                double sy = rand.Next(2) == 0 ? 1 : -1;
                double sx = rand.Next(2) == 0 ? 1 : -1;
                pairs.Add(((FP)(sy * magY), (FP)(sx * magX)));
            }

            foreach (var (y, x) in pairs)
            {
                double yd = (double)y;
                double xd = (double)x;
                FP expected = (FP)Math.Atan2(yd, xd);
                FP got = FP.Atan2(y, x);
                stat.Acc(expected, got, AbsTolAtan);
            }

            Log("ATAN2", stat, AbsTolAtan*4000);
            Assert.LessOrEqual((double)stat.maxAbs, (double)AbsTolAtan * 4000, "Atan2 最大绝对误差超限");
        }

        static void Log(string name, Stat s, FP tol)
        {
            string msg = $"{name} -> {s} Tol={tol}";
#if UNITY_2021_1_OR_NEWER
            Debug.Log(msg);
#else
            Console.WriteLine(msg);
#endif
        }
    }
}
