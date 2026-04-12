namespace SBridge

module FunctionLists =
    // LinkFunction(in Binary)
    let linkFunctionList =
        [
            "deregister_tm_clones";
            "register_tm_clones";
            "__do_global_dtors_aux";
            "frame_dummy";
            "call_weak_fn";
            "__libc_csu_init";
            "_fini";
            "__gmon_start__";
            "_init";
            "_DT_INIT";
            "_DT_FINI";
            "_FINI_0";
            "__libc_csu_fini";
            "_start";
            "__cxa_finalize";
            "__cxa_atexit";
        ]

    // function Preix
    let functionPrefixList = [
        "__isoc99_"; "__isoc11_" ;"__posix_"; "__gnu_"; "__x86_64_sysv_"; "__glibc_"; "__pthread_"; "__builtin_"; "__locale_"; "__asan_"; "__ubsan_"; "__extension__"; "__sanitizer_";
    ]

    // excludedSymbols (ida, ghidra, decompiler, preprocessing symbols)
    let excludedSymbols = [
        // IDA Pro symbols(https://hex-rays.com/blog/igors-tip-of-the-week-67-decompiler-helpers)
        "LOBYTE"; "HIBYTE"; "LOWORD"; "HIWORD"; "LODWORD"; "HIDWORD";
        "BYTE1"; "BYTE2"; "BYTE3"; "BYTE4"; "BYTE5"; "BYTE6"; "BYTE7"; "BYTE8"; "BYTE9"; "BYTE10"; "BYTE11"; "BYTE12"; "BYTE13"; "BYTE14"; "BYTE15";
        "WORD1"; "WORD2"; "WORD3"; "WORD4"; "WORD5"; "WORD6"; "WORD7";
        "SBYTE1"; "SBYTE2"; "SBYTE3"; "SBYTE4"; "SBYTE5"; "SBYTE6"; "SBYTE7"; "SBYTE8"; "SBYTE9"; "SBYTE10"; "SBYTE11"; "SBYTE12"; "SBYTE13"; "SBYTE14"; "SBYTE15";
        "SWORD1"; "SWORD2"; "SWORD3"; "SWORD4"; "SWORD5"; "SWORD6"; "SWORD7";
        "SLOWORD"; "SLODWORD"; "SHIWORD"; "SHIDWORD";
        "__PAIR__"; "__PAIR16__"; "__PAIR32__"; "__PAIR64__"; "__PAIR128__"; "__SPAIR16__";
        "__ROL__"; "__ROR__"; "__ROL1__"; "__ROL2__"; "__ROL4__"; "__ROL8__";
        "__ROR1__"; "__ROR2__"; "__ROR4__"; "__ROR8__";
        "__MKCSHL__"; "__MKCSHR__"; "__SETS__"; "__OFADD__"; "__OFSUB__"; "__CFADD__"; "__CFSUB__";
        "__RCL__"; "__RCR__"; "__MKCRCL__"; "__MKCRCR__"; "__SETP__";
        "COERCE_FLOAT"; "COERCE_DOUBLE"; "COERCE_LONG_DOUBLE"; "COERCE_UNSIGNED_INT"; "COERCE_UNSIGNED_INT64";
        "SLODWORD";

        // Ghidra P-code
        "CONCAT"; "CONCAT11"; "CONCAT12"; "CONCAT13"; "CONCAT14"; "CONCAT15"; "CONCAT16"; "CONCAT21"; "CONCAT22"; "CONCAT24"; "CONCAT28";
        "CONCAT31"; "CONCAT32"; "CONCAT34"; "CONCAT38"; "CONCAT41"; "CONCAT42"; "CONCAT44"; "CONCAT48"; "CONCAT412"; "CONCAT416";
        "CONCAT51"; "CONCAT52"; "CONCAT54"; "CONCAT58"; "CONCAT61"; "CONCAT62"; "CONCAT64"; "CONCAT68"; "CONCAT71"; "CONCAT72";
        "CONCAT74"; "CONCAT78"; "CONCAT81"; "CONCAT82"; "CONCAT84"; "CONCAT88";
        "ZEXT"; "SEXT"; "EXTRACT"; "SUBPIECE"; "MULTIEQUAL"; "INDIRECT"; "PIECE"; "JUMPOUT"; "LZCOUNT"; "CANARY"; "SCARRY";
        "ROUND"; "TRUNC"; "CARRY4"; "CARRY8"; "CARRY16";

        // Compiler & Linker & Debugging Symbols
        "__libc_start_main"; "__do_global_dtors_aux"; "__do_global_ctors_aux"; "frame_dummy";
        "deregister_tm_clones"; "register_tm_clones"; "__libc_csu_init"; "__libc_csu_fini";
        "_init"; "_fini"; "_start"; "__cxa_finalize"; "__cxa_atexit";
        "__stack_chk_guard"; "__stack_chk_fail_local"; "__stack_chk_fail"; "__gmon_start__";
        "_BYTE"; "_WORD"; "_DWORD"; "_QWORD"; "_LONGLONG"; "_BOOL1"; "_BOOL2"; "_BOOL4"; "_UNKNOWN";
        "__cdecl"; "__stdcall"; "__fastcall"; "__thiscall"; "__vectorcall"; "__usercall"; "__userpurge";
        "__nop"; "__debugbreak"; "__int2c"; "__inbyte"; "__inword"; "__indword";
        "__outbyte"; "__outword"; "__outdword"; "_Static_assert";
        "__CASSERT_N0__"; "__CASSERT_N1__"; "CASSERT"; "__attribute__"; "__builtin_";
        "__PRETTY_FUNCTION__"; "__FUNCTION__"; "__func__"; "_gmon_start_"; "__ctype_b_loc";

        // MSVC (https://learn.microsoft.com/ko-kr/cpp/intrinsics/readfsbyte-readfsdword-readfsqword-readfsword)
        "__readfsbyte"; "__readfsword"; "__readfsdword"; "__readfsqword";
        "__writefsbyte"; "__writefsword"; "__writefsdword"; "__writefsqword"; "__fastfail";
    ]




    // Libc Optimization at LLVM / SimplifyLibCalls.cpp 
    // https://sourceware.org/glibc/manual/2.42/html_mono/libc.html)
    let libcNormalization = Map [
        // LLVM Simplify LibCalls
        ("printf",  ["printf"; "iprintf"; "puts"; "putchar";])
        ("fprintf",  ["fprintf"; "fputc"; "fputs"; "fwrite"; "fiprintf";"__small_fprintf"])
        ("strncat",  ["strcat"; "strncat"])
        ("strrchr",  ["strrchr"; "strchr"; "strlen"; "strpbrk"])
        ("strncmp",  ["strcmp"; "memcmp"; "strncmp"; "strstr"])
        ("memcopy",  ["memcpy"; "bcopy"; "memset"; "strncpy"; "stpncpy"; "bcmp"; "memccpy"])
        // GNU C Library macro
        ("gettext", ["gettext"; "dgettext"; "dcgettext";"ngettext";"dngettext";"dcngettext"])
        ("errno", ["errno"; "__errno_location"])
    ]


    // libc functions
    // https://www.ibm.com/docs/en/i/7.6.0?topic=extensions-standard-c-library-functions-table-by-name
    // https://sourceware.org/glibc/manual/2.42/html_mono/libc.html)
    let libcFunctions = [
        "__assert_fail"; "abort"; "abs"; "accept"; "accept4"; "acos"; "asctime"; "asctime_r"; "asin"; "assert"; "atan"; "atan2"; "atexit"; "atof"; "atoi"; "atol";
        "bcmp"; "bcopy"; "bsearch"; "btowc"; "calloc"; "catclose"; "catgets"; "catopen"; "ceil"; "clearerr"; "clock"; "close"; "cos"; "cosh";
        "ctime"; "ctime64"; "ctime64_r"; "ctime_r"; "dcgettext"; "dcngettext"; "dgettext"; "difftime"; "difftime64"; "div"; "dngettext"; "dprintf"; "erf"; "erfc"; "errno"; "__errno_location"; "exit"; "exp"; "fabs";
        "fclose"; "fdopen"; "feof"; "ferror"; "fflush"; "fgetc"; "fgetpos"; "fgets"; "fgetwc"; "fgetws"; "fileno"; "fiprintf"; "floor";
        "fmod"; "fopen"; "fprintf"; "fputc"; "fputc_unlocked"; "fputs"; "fputwc"; "fputws"; "fputs_unlocked"; "fread"; "free"; "freeaddrinfo"; "freopen"; "frexp"; "fscanf";
        "fseek"; "fsetpos"; "ftell"; "fwide"; "fwprintf"; "fwrite"; "fwscanf"; "gamma"; "getc"; "getc_unlocked";"getaddrinfo"; "getchar"; "getchar_unlocked"; "getenv";
        "getnameinfo"; "getsockopt"; "gets"; "gettext"; "getw"; "getwc"; "getwchar"; "gmtime"; "gmtime64"; "gmtime64_r"; "gmtime_r"; "hypot"; "inet_aton";
        "inet_pton"; "iprintf"; "isalnum"; "isalpha"; "isascii"; "isblank"; "iscntrl"; "isdigit"; "isgraph"; "islower"; "isprint"; "ispunct"; "isspace"; "isupper";
        "iswalnum"; "iswalpha"; "iswblank"; "iswcntrl"; "iswctype"; "iswdigit"; "iswgraph"; "iswlower"; "iswprint";
        "iswpunct"; "iswspace"; "iswupper"; "iswxdigit"; "isxdigit"; "j0"; "j1"; "jn"; "labs"; "ldexp"; "ldiv";
        "localeconv"; "localtime"; "localtime64"; "localtime64_r"; "localtime_r"; "log"; "log10"; "longjmp"; "malloc"; "memalign";
        "mblen"; "mbrlen"; "mbrtowc"; "mbsinit"; "mbsrtowc"; "mbstowcs"; "mbtowc"; "memccpy"; "memchr"; "memcmp"; "memcpy"; "memmove";
        "memset"; "mktime"; "mktime64"; "modf"; "ngettext"; "nextafter"; "nextafterl"; "nexttoward"; "nexttowardl"; "nl_langinfo";
        "open"; "perror"; "pow"; "pread"; "pread64"; "printf"; "putc"; "putchar"; "putenv"; "puts"; "putw"; "putwc"; "putwchar"; "putws"; "pwrite"; "pwrite64"; "qsort"; "quantexpd128";
        "quantexpd32"; "quantexpd64"; "quantized128"; "quantized32"; "quantized64"; "raise"; "rand"; "rand_r"; "read"; "readv"; "realloc"; "reallocarray";
        "recv"; "recvfrom"; "recvmsg"; "regcomp"; "regerror"; "regexec"; "regfree"; "remove"; "rename"; "rewind"; "samequantumd128"; "samequantumd32"; "samequantumd64";
        "scanf"; "send"; "sendmsg"; "sendto"; "setbuf"; "setjmp"; "setlocale"; "setsockopt"; "setvbuf"; "signal"; "__small_fprintf"; "sin"; "sinh"; "snprintf";
        "socket"; "socketpair"; "sprintf"; "sqrt"; "srand"; "sscanf"; "stpncpy"; "strcasecmp"; "strcat"; "strchr"; "strcmp"; "strcoll"; "strcpy"; "strcspn";
        "strerror"; "strfmon"; "strftime"; "strlen"; "strncasecmp"; "strncat"; "strncmp"; "strncpy"; "strnlen"; "strpbrk"; "strptime";
        "strrchr"; "strspn"; "strstr"; "strtod"; "strtod128"; "strtod32"; "strtod64"; "strtof"; "strtok"; "strtok_r";
        "strtol"; "strtold"; "strtoul"; "strxfrm"; "swprintf"; "swscanf"; "system"; "tan"; "tanh"; "textdomain"; "time"; "time64";
        "tmpfile"; "tmpnam"; "toascii"; "tolower"; "toupper"; "towctrans"; "towlower"; "towupper"; "ungetc"; "ungetwc";
        "va_arg"; "va_copy"; "va_end"; "va_start"; "vfprintf"; "vfscanf"; "vfwprintf"; "vfwscanf"; "vprintf"; "vscanf";
        "vsnprintf"; "vsprintf"; "vsscanf"; "vswprintf"; "vswscanf"; "vwprintf"; "vwscanf"; "wcrtomb"; "wcscat"; "wcschr";
        "wcscmp"; "wcscoll"; "wcscpy"; "wcscspn"; "wcsftime"; "wcslen"; "wcsncat"; "wcsncmp"; "wcsncpy"; "wcspbrk";
        "wcsrchr"; "wcsrtombs"; "wcsspn"; "wcsstr"; "wcstod"; "wcstod128"; "wcstod32"; "wcstod64"; "wcstof"; "wcstok";
        "wcstol"; "wcstold"; "wcstombs"; "wcstoul"; "wcsxfrm"; "wctob"; "wctomb"; "wctrans"; "wctype"; "wcwidth"; "wmemchr";
        "wmemcmp"; "wmemcpy"; "wmemmove"; "wmemset"; "wprintf"; "write"; "writev"; "wscanf"; "y0"; "y1"; "yn";
    ]
