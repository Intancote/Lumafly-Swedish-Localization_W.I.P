﻿using System.Collections.Generic;

namespace Lumafly.Enums;

public enum SupportedLanguages
{
    en,
    es,
    pt,
    fr,
    zh,
    ru,
    pl,
    ja,
    sv,
}

public static class SupportedLanguagesInfo
{
    public static readonly Dictionary<SupportedLanguages, string> SupportedLangToCulture = new()
    {
        { SupportedLanguages.en, "en-US" },
        { SupportedLanguages.es, "es-ES" },
        { SupportedLanguages.pt, "pt-BR" },
        { SupportedLanguages.fr, "fr-FR" },
        { SupportedLanguages.zh, "zh-CN" },
        { SupportedLanguages.ru, "ru-RU" },
        { SupportedLanguages.pl, "pl-PL" },
        { SupportedLanguages.ja, "ja-JP" },
        { SupportedLanguages.sv, "sv-SE" },
    };

    public static readonly Dictionary<SupportedLanguages, string> LocalizedLanguageOptions = new()
    {
        { SupportedLanguages.en, "English" },
        { SupportedLanguages.es, "Español" },
        { SupportedLanguages.pt, "Português" },
        { SupportedLanguages.fr, "Français" },
        { SupportedLanguages.zh, "中文" },
        { SupportedLanguages.ru, "Русский" },
        { SupportedLanguages.pl, "Polski" },
        { SupportedLanguages.ja, "日本語" },
        { SupportedLanguages.sv, "Svenska" },
    };
}
