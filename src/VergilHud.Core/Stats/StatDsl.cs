using System.Globalization;
using System.Text;

namespace VergilHud.Core;

public sealed class DslException : Exception
{
    public int Position { get; }
    public DslException(string message, int position) : base($"{message} (posición {position})") => Position = position;
}

/// <summary>SQL ya compilado. Todos los literales viajan como parámetros: nunca se interpolan en el texto SQL.</summary>
public sealed record CompiledExpr(string Sql, IReadOnlyDictionary<string, object> Parameters);

/// <summary>
/// Mini-lenguaje de estadísticas (DESIGN.md §6.2).
/// Gramática:  or := and ('||' and)* ; and := not ('&amp;&amp;' not)* ; not := '!' not | cmp ;
/// cmp := add (('=='|'!='|'&lt;'|'&lt;='|'&gt;'|'&gt;=') add)? | IDENT 'in' '[' lit (',' lit)* ']' ;
/// add := mul (('+'|'-') mul)* ; mul := atom ('*' atom)* ; atom := IDENT | NUMBER | STRING | 'true' | 'false' | '(' or ')' | '-' NUMBER.
/// Los identificadores solo pueden ser hechos del <see cref="FactCatalog"/>.
/// </summary>
public static class StatDsl
{
    enum T { Ident, Number, String, And, Or, Not, Eq, Ne, Lt, Le, Gt, Ge, LParen, RParen, LBracket, RBracket, Comma, Plus, Minus, Star, End }

    readonly record struct Tok(T Type, string Text, int Pos);

    abstract record Node(int Pos);
    sealed record IdentNode(string Name, int Pos) : Node(Pos);
    sealed record NumNode(double Value, int Pos) : Node(Pos);
    sealed record StrNode(string Value, int Pos) : Node(Pos);
    sealed record NotNode(Node Operand, int Pos) : Node(Pos);
    sealed record BinNode(T Op, Node L, Node R, int Pos) : Node(Pos);
    sealed record InNode(Node Target, List<Node> Items, int Pos) : Node(Pos);

    /// <summary>Compila la expresión. <paramref name="paramPrefix"/> evita choques de nombres entre varias expresiones de una consulta.</summary>
    public static CompiledExpr Compile(string source, string paramPrefix = "p")
    {
        if (string.IsNullOrWhiteSpace(source)) throw new DslException("Expresión vacía", 0);
        var toks = Lex(source);
        var parser = new Parser(toks);
        var ast = parser.ParseTop();
        var emitter = new Emitter(paramPrefix);
        var (sql, ty) = emitter.Emit(ast);
        if (ty == Ty.Text) throw new DslException("La expresión debe ser numérica o una condición, no texto", 0);
        return new CompiledExpr($"COALESCE({sql}, 0)", emitter.Params);
    }

    /// <summary>
    /// Compila la expresión a un predicado que se evalúa en memoria sobre los hechos de UN jugador en UNA mano (para las reglas de
    /// autonotas). Imita la lógica de tres valores de SQL: un hecho vacío (NULL) hace que la condición no se cumpla, igual que
    /// con COALESCE(expr, 0) en las consultas. Lanza DslException con la misma validación que <see cref="Compile"/>.
    /// </summary>
    public static Func<PlayerHandFacts, bool> CompilePredicate(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new DslException("Expresión vacía", 0);
        var ast = new Parser(Lex(source)).ParseTop();
        var (_, ty) = new Emitter("v").Emit(ast);   // misma validación de tipos e identificadores que el SQL
        if (ty == Ty.Text) throw new DslException("La expresión debe ser numérica o una condición, no texto", 0);
        return f => Eval(ast, f) is double d && d != 0;
    }

    static bool? ToBool(object? x) => x switch { null => null, double d => d != 0, _ => false };

    static object? Eval(Node n, PlayerHandFacts f)
    {
        switch (n)
        {
            case IdentNode id:
                if (id.Name == "true") return 1.0;
                if (id.Name == "false") return 0.0;
                var v = f[id.Name];
                return FactCatalog.Get(id.Name).Kind == FactKind.Text ? v as string : v == null ? null : Convert.ToDouble(v);
            case NumNode num: return num.Value;
            case StrNode s: return s.Value;
            case NotNode not:
            {
                var b = ToBool(Eval(not.Operand, f));
                return b == null ? null : b.Value ? 0.0 : 1.0;
            }
            case InNode inn:
            {
                var t = Eval(inn.Target, f);
                if (t == null) return null;
                foreach (var item in inn.Items)
                    if (Equals(t, Eval(item, f))) return 1.0;
                return 0.0;
            }
            case BinNode b:
            {
                var l = Eval(b.L, f);
                var r = Eval(b.R, f);
                switch (b.Op)
                {
                    case T.And:
                    {
                        var x = ToBool(l); var y = ToBool(r);
                        if (x == false || y == false) return 0.0;
                        return x == null || y == null ? null : 1.0;
                    }
                    case T.Or:
                    {
                        var x = ToBool(l); var y = ToBool(r);
                        if (x == true || y == true) return 1.0;
                        return x == null || y == null ? null : 0.0;
                    }
                    case T.Plus or T.Minus or T.Star:
                        if (l is not double a || r is not double c) return null;
                        return b.Op == T.Plus ? a + c : b.Op == T.Minus ? a - c : a * c;
                    default:
                        if (l == null || r == null) return null;
                        if (l is string ls && r is string rs)
                            return (b.Op == T.Eq ? ls == rs : ls != rs) ? 1.0 : 0.0;
                        if (l is double x1 && r is double y1)
                            return (b.Op switch { T.Eq => x1 == y1, T.Ne => x1 != y1, T.Lt => x1 < y1, T.Le => x1 <= y1, T.Gt => x1 > y1, _ => x1 >= y1 }) ? 1.0 : 0.0;
                        return null;
                }
            }
        }
        return null;
    }

    // ---------------- léxico ----------------
    static List<Tok> Lex(string s)
    {
        var r = new List<Tok>();
        int i = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            int start = i;
            if (char.IsLetter(c) || c == '_')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                r.Add(new Tok(T.Ident, s[start..i], start));
                continue;
            }
            if (char.IsDigit(c))
            {
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                r.Add(new Tok(T.Number, s[start..i], start));
                continue;
            }
            if (c == '"' || c == '\'')
            {
                i++;
                int st = i;
                while (i < s.Length && s[i] != c) i++;
                if (i >= s.Length) throw new DslException("Cadena sin cerrar", start);
                r.Add(new Tok(T.String, s[st..i], start));
                i++;
                continue;
            }
            string two = i + 1 < s.Length ? s.Substring(i, 2) : "";
            T? t2 = two switch { "&&" => T.And, "||" => T.Or, "==" => T.Eq, "!=" => T.Ne, "<=" => T.Le, ">=" => T.Ge, _ => null };
            if (t2 != null) { r.Add(new Tok(t2.Value, two, start)); i += 2; continue; }
            T? t1 = c switch
            {
                '!' => T.Not, '<' => T.Lt, '>' => T.Gt, '(' => T.LParen, ')' => T.RParen, '[' => T.LBracket,
                ']' => T.RBracket, ',' => T.Comma, '+' => T.Plus, '-' => T.Minus, '*' => T.Star, _ => null,
            };
            if (t1 == null) throw new DslException($"Carácter no permitido '{c}'", start);
            r.Add(new Tok(t1.Value, c.ToString(), start));
            i++;
        }
        r.Add(new Tok(T.End, "", s.Length));
        return r;
    }

    // ---------------- sintaxis ----------------
    sealed class Parser
    {
        readonly List<Tok> _t;
        int _i;
        public Parser(List<Tok> t) => _t = t;
        Tok Cur => _t[_i];
        Tok Next() => _t[_i++];

        bool Accept(T type)
        {
            if (Cur.Type != type) return false;
            _i++;
            return true;
        }

        void Expect(T type, string what)
        {
            if (!Accept(type)) throw new DslException($"Se esperaba {what}", Cur.Pos);
        }

        public Node ParseTop()
        {
            var n = Or();
            if (Cur.Type != T.End) throw new DslException($"Sobra '{Cur.Text}'", Cur.Pos);
            return n;
        }

        Node Or()
        {
            var l = And();
            while (Cur.Type == T.Or) { var p = Next().Pos; l = new BinNode(T.Or, l, And(), p); }
            return l;
        }

        Node And()
        {
            var l = Not();
            while (Cur.Type == T.And) { var p = Next().Pos; l = new BinNode(T.And, l, Not(), p); }
            return l;
        }

        Node Not()
        {
            if (Cur.Type == T.Not) { var p = Next().Pos; return new NotNode(Not(), p); }
            return Cmp();
        }

        Node Cmp()
        {
            var l = Add();
            if (Cur.Type == T.Ident && Cur.Text == "in")
            {
                var p = Next().Pos;
                Expect(T.LBracket, "'['");
                var items = new List<Node> { Atom() };
                while (Accept(T.Comma)) items.Add(Atom());
                Expect(T.RBracket, "']'");
                return new InNode(l, items, p);
            }
            if (Cur.Type is T.Eq or T.Ne or T.Lt or T.Le or T.Gt or T.Ge)
            {
                var op = Next();
                return new BinNode(op.Type, l, Add(), op.Pos);
            }
            return l;
        }

        Node Add()
        {
            var l = Mul();
            while (Cur.Type is T.Plus or T.Minus) { var op = Next(); l = new BinNode(op.Type, l, Mul(), op.Pos); }
            return l;
        }

        Node Mul()
        {
            var l = Atom();
            while (Cur.Type == T.Star) { var op = Next(); l = new BinNode(T.Star, l, Atom(), op.Pos); }
            return l;
        }

        Node Atom()
        {
            var t = Cur;
            switch (t.Type)
            {
                case T.Ident: _i++; return new IdentNode(t.Text, t.Pos);
                case T.Number: _i++; return new NumNode(ParseNum(t), t.Pos);
                case T.String: _i++; return new StrNode(t.Text, t.Pos);
                case T.Minus:
                    _i++;
                    if (Cur.Type != T.Number) throw new DslException("Tras '-' se esperaba un número", Cur.Pos);
                    var num = Next();
                    return new NumNode(-ParseNum(num), t.Pos);
                case T.LParen:
                    _i++;
                    var n = Or();
                    Expect(T.RParen, "')'");
                    return n;
                default:
                    throw new DslException(t.Type == T.End ? "Expresión incompleta" : $"No se esperaba '{t.Text}'", t.Pos);
            }
        }

        static double ParseNum(Tok t) =>
            double.TryParse(t.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
                ? d : throw new DslException($"Número inválido '{t.Text}'", t.Pos);
    }

    // ---------------- generación de SQL ----------------
    enum Ty { Bool, Num, Text }

    sealed class Emitter
    {
        readonly string _prefix;
        public Dictionary<string, object> Params { get; } = new();
        public Emitter(string prefix) => _prefix = prefix;

        string Param(object value)
        {
            var name = $"${_prefix}{Params.Count}";
            Params[name] = value;
            return name;
        }

        static string ToBool(string sql, Ty ty, int pos) => ty switch
        {
            Ty.Bool => sql,
            Ty.Num => $"({sql} <> 0)",
            _ => throw new DslException("Se esperaba una condición, no texto", pos),
        };

        public (string Sql, Ty Ty) Emit(Node n)
        {
            switch (n)
            {
                case IdentNode id:
                    if (id.Name == "true") return ("1", Ty.Bool);
                    if (id.Name == "false") return ("0", Ty.Bool);
                    if (!FactCatalog.Contains(id.Name))
                        throw new DslException($"Hecho desconocido '{id.Name}'", id.Pos);
                    // El nombre viene de la lista blanca del catálogo: es seguro como identificador SQL.
                    return (id.Name, FactCatalog.Get(id.Name).Kind == FactKind.Text ? Ty.Text : Ty.Num);
                case NumNode num: return (Param(num.Value), Ty.Num);
                case StrNode str: return (Param(str.Value), Ty.Text);
                case NotNode not:
                {
                    var (s, t) = Emit(not.Operand);
                    return ($"(NOT {ToBool(s, t, not.Pos)})", Ty.Bool);
                }
                case InNode inn:
                {
                    var (ts, tt) = Emit(inn.Target);
                    if (inn.Target is not IdentNode) throw new DslException("'in' solo se aplica a un hecho", inn.Pos);
                    var parts = new List<string>();
                    foreach (var item in inn.Items)
                    {
                        var (s, t) = Emit(item);
                        if (item is not (NumNode or StrNode)) throw new DslException("La lista de 'in' solo admite literales", item.Pos);
                        if ((t == Ty.Text) != (tt == Ty.Text)) throw new DslException("Tipos incompatibles en 'in'", item.Pos);
                        parts.Add(s);
                    }
                    return ($"({ts} IN ({string.Join(", ", parts)}))", Ty.Bool);
                }
                case BinNode b: return EmitBin(b);
            }
            throw new DslException("Expresión no soportada", n.Pos);
        }

        (string, Ty) EmitBin(BinNode b)
        {
            var (ls, lt) = Emit(b.L);
            var (rs, rt) = Emit(b.R);
            switch (b.Op)
            {
                case T.And:
                    return ($"({ToBool(ls, lt, b.Pos)} AND {ToBool(rs, rt, b.Pos)})", Ty.Bool);
                case T.Or:
                    return ($"({ToBool(ls, lt, b.Pos)} OR {ToBool(rs, rt, b.Pos)})", Ty.Bool);
                case T.Plus:
                case T.Minus:
                case T.Star:
                    if (lt == Ty.Text || rt == Ty.Text) throw new DslException("No se puede operar con texto", b.Pos);
                    var sym = b.Op == T.Plus ? "+" : b.Op == T.Minus ? "-" : "*";
                    return ($"({ls} {sym} {rs})", Ty.Num);
                default:
                    bool text = lt == Ty.Text || rt == Ty.Text;
                    if (text)
                    {
                        if (lt != rt) throw new DslException("No se puede comparar texto con número", b.Pos);
                        if (b.Op is not (T.Eq or T.Ne)) throw new DslException("El texto solo admite == y !=", b.Pos);
                    }
                    var op = b.Op switch { T.Eq => "=", T.Ne => "<>", T.Lt => "<", T.Le => "<=", T.Gt => ">", _ => ">=" };
                    return ($"({ls} {op} {rs})", Ty.Bool);
            }
        }
    }
}
