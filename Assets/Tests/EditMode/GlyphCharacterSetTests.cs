using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using NetworkExample.UnityDemo.Text3D;
using NUnit.Framework;

namespace NetworkExample.UnityDemo.Tests.EditMode
{
    public sealed class GlyphCharacterSetTests
    {
        [Test]
        public void Parse_DefaultClass_HoldsDigitsAndLatinLettersInCodepointOrder()
        {
            GlyphCharacterSet set = GlyphCharacterSet.Parse(WaxGlyphBlock.DefaultCharacterSet);

            Assert.That(set.Count, Is.EqualTo(62));
            Assert.That(set.RangeCount, Is.EqualTo(3));
            Assert.That(Text(set), Is.EqualTo("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"));
            for (int i = 0; i < set.Count; i++)
            {
                Assert.That(set.Contains(set.CodepointAt(i)), Is.True);
            }

            Assert.That(set.Contains('_'), Is.False);
            Assert.That(set.Contains('{'), Is.False);
        }

        [Test]
        public void Parse_LiteralsEscapesAndEdgeDashes()
        {
            GlyphCharacterSet set = GlyphCharacterSet.Parse(@"[-a\]一-丂z-]");

            Assert.That(Text(set), Is.EqualTo("-]az一丁丂"));
        }

        [Test]
        public void Parse_OverlappingAndAdjacentRanges_Merge()
        {
            GlyphCharacterSet set = GlyphCharacterSet.Parse("[a-fc-mn-z]");

            Assert.That(set.Count, Is.EqualTo(26));
            Assert.That(set.RangeCount, Is.EqualTo(1));
        }

        [Test]
        public void Parse_CharacterOutsideTheBasicPlane_IsOneCharacter()
        {
            GlyphCharacterSet set = GlyphCharacterSet.Parse("[𠀀-𠀂]");

            Assert.That(set.Count, Is.EqualTo(3));
            Assert.That(set.CodepointAt(0), Is.EqualTo(0x20000));
        }

        [TestCase("a-z")]
        [TestCase("[a-z")]
        [TestCase("[]")]
        [TestCase("[^a-z]")]
        [TestCase("[z-a]")]
        [TestCase(@"[\d]")]
        [TestCase(@"[a\]")]
        [TestCase(@"[\u12]")]
        [TestCase("[a[b]")]
        [TestCase("[a]b]")]
        public void Parse_Malformed_Throws(string pattern)
        {
            Assert.Throws<FormatException>(() => GlyphCharacterSet.Parse(pattern));
        }

        [Test]
        public void Mix_MatchesSplitMix64()
        {
            // The first output of a splitmix64 generator seeded with 0.
            Assert.That(GlyphCharacterSet.Mix(0), Is.EqualTo(0xE220A8397B1DCDAFUL));
        }

        [Test]
        public void Pick_NetIds_GiveTheCharactersEveryClientShows()
        {
            // Every client picks a glyph block's character from its net id alone, so these are the
            // characters all of them show for net ids 1 to 20. A platform, scripting backend or change
            // that picks differently fails here rather than showing players different characters.
            GlyphCharacterSet set = GlyphCharacterSet.Parse(WaxGlyphBlock.DefaultCharacterSet);
            var picked = new StringBuilder();
            for (uint netId = 1; netId <= 20; netId++)
            {
                picked.Append(char.ConvertFromUtf32(set.Pick(netId)));
            }

            Assert.That(picked.ToString(), Is.EqualTo("pQDwuQxgY0lp5OrBfKcY"));
            Assert.That(set.Pick(4000000000u), Is.EqualTo('I'), "a net id near the top of the range");
        }

        [Test]
        public void Pick_ConsecutiveNetIds_AreStableAndSpreadAcrossTheSet()
        {
            GlyphCharacterSet set = GlyphCharacterSet.Parse(WaxGlyphBlock.DefaultCharacterSet);
            const int seeds = 10000;
            var counts = new Dictionary<int, int>();
            for (uint netId = 1; netId <= seeds; netId++)
            {
                int codepoint = set.Pick(netId);
                Assert.That(set.Pick(netId), Is.EqualTo(codepoint), "the same seed picks the same character");
                counts.TryGetValue(codepoint, out int count);
                counts[codepoint] = count + 1;
            }

            double expected = (double)seeds / set.Count;
            Assert.That(counts.Count, Is.EqualTo(set.Count), "every character is picked");
            Assert.That(counts.Values.Min(), Is.GreaterThan(expected * 0.6));
            Assert.That(counts.Values.Max(), Is.LessThan(expected * 1.4));
        }

        private static string Text(GlyphCharacterSet set)
        {
            var builder = new StringBuilder();
            foreach (int codepoint in set.EnumerateCodepoints())
            {
                builder.Append(char.ConvertFromUtf32(codepoint));
            }

            return builder.ToString();
        }
    }
}
