namespace Pennyworth.Assistant.Tests;

public class AnonymizerTests
{
    private static Anonymizer Ana() => new("ana.souza", @"C:\Users\José da Silva-Neto", "DESKTOP-ANA");

    [Fact]
    public void Hides_the_profile_and_personal_folders_but_keeps_known_ones()
    {
        string text = Ana().Anonymize(@"Temp is at C:\Users\José da Silva-Neto\AppData\Local\Temp (18 GB).");

        Assert.Equal(@"Temp is at <profile>\AppData\Local\Temp (18 GB).", text);
    }

    [Fact]
    public void Hides_names_with_spaces_accents_and_hyphens_whole()
    {
        var anonymizer = Ana();

        string text = anonymizer.Anonymize(@"Item: Relatório final.docx (from C:\Users\José da Silva-Neto\Documents\Projeto Ana Lúcia\Relatório final.docx)");

        Assert.Equal(@"Item: Relatório final.docx (from <profile>\Documents\<folder-1>\<file-1>)", text);
        Assert.DoesNotContain("Lúcia", text);
    }

    [Fact]
    public void The_same_name_always_gets_the_same_placeholder()
    {
        var anonymizer = Ana();

        string first = anonymizer.Anonymize(@"C:\Users\José da Silva-Neto\Videos\Jorge Vercillo\clip.mp4");
        string second = anonymizer.Anonymize(@"C:\Users\José da Silva-Neto\Music\Jorge Vercillo");

        Assert.Equal(@"<profile>\Videos\<folder-1>\<file-1>", first);
        Assert.Equal(@"<profile>\Music\<folder-1>", second);
    }

    [Fact]
    public void Hides_user_machine_and_email_anywhere_but_not_inside_other_words()
    {
        string text = Ana().Anonymize("ana.souza on DESKTOP-ANA wrote to ana.souza@mail.com; banana.souzas stays; josé da silva-neto too.");

        Assert.Equal("<user> on <pc> wrote to <email-1>; banana.souzas stays; <user> too.", text);
    }

    [Fact]
    public void A_sentence_ending_after_a_path_is_not_swallowed()
    {
        string text = Ana().Anonymize(@"It sits in C:\Users\José da Silva-Neto\Downloads. Empty it later.");

        Assert.Equal(@"It sits in <profile>\Downloads. Empty it later.", text);
    }

    [Fact]
    public void Restores_names_for_display()
    {
        var anonymizer = Ana();
        string sent = anonymizer.Anonymize(@"Hello ana.souza, the biggest folder (C:\Users\José da Silva-Neto\Videos\Jorge Vercillo) is 6 GB.");

        string answer = @"Dear <user>, the folder <profile>\Videos\<folder-1> is large; <folder-9> is unknown.";

        Assert.Equal(@"Dear ana.souza, the folder C:\Users\José da Silva-Neto\Videos\Jorge Vercillo is large; <folder-9> is unknown.", anonymizer.Restore(answer));
        Assert.DoesNotContain("Vercillo", sent);
    }

    [Theory]
    [InlineData(@"C:\Users\ana\Divórcio-2026\pensão.docx", @"<profile>\<folder-1>\<file-1>")]
    [InlineData("C:/Users/ana/Divórcio-2026/pensão.docx", "<profile>/<folder-1>/<file-1>")]
    public void Hides_personal_folders_whatever_the_path_separator(string input, string expected)
    {
        var anonymizer = new Anonymizer("ana", @"C:\Users\ana", "PC1");

        Assert.Equal(expected, anonymizer.Anonymize(input));
    }

    [Fact]
    public void Hides_a_posix_profile()
    {
        var anonymizer = new Anonymizer("ana", "/home/ana", "PC1");

        Assert.Equal("<profile>/<folder-1>/<file-1>", anonymizer.Anonymize("/home/ana/Divórcio-2026/pensão.docx"));
    }

    [Fact]
    public void Hides_the_profile_itself_whatever_the_separator()
    {
        var anonymizer = new Anonymizer("ana", @"C:\Users\ana", "PC1");

        Assert.Equal("See <profile> now", anonymizer.Anonymize("See C:/Users/ana now"));
    }

    [Fact]
    public void Mixed_separators_still_hide_personal_folders()
    {
        string text = new Anonymizer("ana", @"C:\Users\ana", "PC1").Anonymize(@"C:\Users\ana/Divórcio-2026/doc.docx");

        Assert.DoesNotContain("Divórcio", text);
    }

    [Fact]
    public void OneDrive_and_other_profile_paths_work_the_same()
    {
        string text = new Anonymizer("bob", @"C:\Users\bob", "PC1").Anonymize(@"C:\Users\Bob\OneDrive\Fotos da Praia\IMG_0001.jpg");

        Assert.Equal(@"<profile>\OneDrive\<folder-1>\<file-1>", text);
    }
}
