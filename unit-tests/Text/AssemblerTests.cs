using System.Text;
using Loyc.MiniTest;
using Pixie;

namespace Wasm.Text
{
    [TestFixture]
    public class AssemblerTests
    {
        [Test]
        public void AssembleEmptyModule()
        {
            var module = AssembleModule("(module)");
            Assert.AreEqual(0, module.Sections.Count);
        }

        [Test]
        public void AssembleNamedEmptyModule()
        {
            var module = AssembleModule("(module $test_module)");
            Assert.AreEqual(1, module.Sections.Count);
            Assert.AreEqual(1, module.GetFirstSectionOrNull<NameSection>().Names.Count);
            Assert.AreEqual("test_module", module.ModuleName);
        }

        [Test]
        public void AssembleModulesWithMemory()
        {
            var module = AssembleModule("(module (memory $mem (limits 10 40)))");
            Assert.AreEqual(1, module.Sections.Count);
            var memSection = module.GetFirstSectionOrNull<MemorySection>();
            Assert.IsNotNull(memSection);
            Assert.AreEqual(1, memSection.Memories.Count);
            var memory = memSection.Memories[0];
            Assert.AreEqual(10u, memory.Limits.Initial);
            Assert.IsTrue(memory.Limits.HasMaximum);
            Assert.AreEqual(40u, memory.Limits.Maximum);

            module = AssembleModule("(module (memory (limits 10)))");
            Assert.AreEqual(1, module.Sections.Count);
            memSection = module.GetFirstSectionOrNull<MemorySection>();
            Assert.IsNotNull(memSection);
            Assert.AreEqual(1, memSection.Memories.Count);
            memory = memSection.Memories[0];
            Assert.AreEqual(10u, memory.Limits.Initial);
            Assert.IsFalse(memory.Limits.HasMaximum);

            module = AssembleModule("(module (memory (data \"hello world\")))");
            Assert.AreEqual(2, module.Sections.Count);
            memSection = module.GetFirstSectionOrNull<MemorySection>();
            Assert.IsNotNull(memSection);
            Assert.AreEqual(1, memSection.Memories.Count);
            memory = memSection.Memories[0];
            Assert.AreEqual(1u, memory.Limits.Initial);
            Assert.IsTrue(memory.Limits.HasMaximum);
            Assert.AreEqual(1u, memory.Limits.Maximum);
            var dataSection = module.GetFirstSectionOrNull<DataSection>();
            Assert.IsNotNull(dataSection);
            Assert.AreEqual(1, dataSection.Segments.Count);
            var segment = dataSection.Segments[0];
            Assert.AreEqual(0u, segment.MemoryIndex);
            Assert.AreEqual("hello world", Encoding.UTF8.GetString(segment.Data));

            module = AssembleModule("(module (memory (import \"mod\" \"mem\") (limits 10 40)))");
            Assert.AreEqual(1, module.Sections.Count);
            var importSection = module.GetFirstSectionOrNull<ImportSection>();
            Assert.IsNotNull(importSection);
            Assert.AreEqual(1, importSection.Imports.Count);
            var import = importSection.Imports[0];
            Assert.AreEqual(ExternalKind.Memory, import.Kind);
            Assert.AreEqual("mod", import.ModuleName);
            Assert.AreEqual("mem", import.FieldName);
            memory = ((ImportedMemory)import).Memory;
            Assert.AreEqual(10u, memory.Limits.Initial);
            Assert.IsTrue(memory.Limits.HasMaximum);
            Assert.AreEqual(40u, memory.Limits.Maximum);
        }

        [Test]
        public void AssembleBadMemoryModules()
        {
            AssertInvalidModule("(module (memory))");
            AssertInvalidModule("(module (memory (limits)))");
            AssertInvalidModule("(module (memory $mem (limits 78359126329586239865823 725357639275693276582334525)))");
            AssertInvalidModule("(module (memory $mem (limits 10e7 10e8)))");
            AssertInvalidModule("(module (memory (limits +10 +40)))");
            AssertInvalidModule("(module (memory $mem1 $mem2 (limits 10 40)))");
            AssertInvalidModule("(module (memory (limits 10 40) (limits 10 40)))");
            AssertInvalidModule("(module (memory (import \"mod\" \"mem\")))");
        }

        private static void AssertInvalidModule(string text)
        {
            Assert.Throws(
                typeof(PixieException),
                () => AssembleModule(text));
        }

        private static WasmFile AssembleModule(string text)
        {
            var log = new TestLog(new[] { Severity.Error }, NullLog.Instance);
            var assembler = new Assembler(log);
            return assembler.AssembleModule(text);
        }
    }
}
