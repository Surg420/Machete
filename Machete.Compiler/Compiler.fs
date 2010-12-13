﻿namespace Machete.Compiler

open Machete

type internal exp = System.Linq.Expressions.Expression
type internal label = System.Linq.Expressions.LabelExpression
type internal methodinfo = System.Reflection.MethodInfo
type internal dyn = Machete.IDynamic

type internal State (element:SourceElement, labelMap:Map<string, label>, breakList:list<label>, continueList:list<label>) = struct
        member this.Element = element
        member this.LabelMap = labelMap
        member this.BreakList = breakList
        member this.ContinueList = continueList
        member this.WithElement element = State (element, labelMap, breakList, continueList)
    end

type Code = IEnvironment -> IArgs -> IDynamic

type Compiler (environment:IEnvironment) =

    let call (e:exp) (m:methodinfo) (a:exp[]) = exp.Call (e, m, a) :> exp
    let invoke (e:exp) (a:exp[]) = exp.Invoke (e, a) :> exp
    let condition test ifTrue ifFalse = exp.Condition (test, ifTrue, ifFalse) :> exp
    let constant v = exp.Constant v :> exp 
    let block es = exp.Block es :> exp      
    
    let environmentParam = exp.Parameter (Reflection.IEnvironment.t, "environment")

    let checkReference (value:Machete.IDynamic) =
        match value with
        | :? IReference as ref ->
            match ref.IsStrictReference, ref.Base, ref.Name with
            | true, :? IEnvironmentRecord, "eval" 
            | true, :? IEnvironmentRecord, "arguments" ->
                environment.ConstructSyntaxError().Op_Throw();
            | _ -> ()
        | _ -> ()

    let simpleAssignment (left:Machete.IDynamic) (right:Machete.IDynamic) =
        checkReference left
        left.Value <- right.Value

    let compoundAssignment (left:Machete.IDynamic) (right:Machete.IDynamic) op =
        let value = op left right
        checkReference left
        left.Value <- value
        value

    let rec evalExpression (state:State) =
        match state.Element with
        | Expression (Nil, right) | ExpressionNoIn (Nil, right) ->
            let inst = evalAssignmentExpression (state.WithElement right)
            let test = exp.TypeIs (inst, Reflection.IReferenceBase.t)
            let ifTrue = call (exp.Convert(inst, Reflection.IReferenceBase.t)) Reflection.IReferenceBase.get [||] 
            let ifFalse = call environmentParam Reflection.IEnvironment.get_Undefined [||]
            condition test ifTrue ifFalse
        | Expression (left, right) | ExpressionNoIn (left, right) ->
            let left = evalExpression (state.WithElement left)
            let inst = evalAssignmentExpression (state.WithElement right)
            let test = exp.TypeIs (inst, Reflection.IReferenceBase.t)
            let ifTrue = call (exp.Convert(inst, Reflection.IReferenceBase.t)) Reflection.IReferenceBase.get [||] 
            let ifFalse = call environmentParam Reflection.IEnvironment.get_Undefined [||]
            block [| left; condition test ifTrue ifFalse |]
            
    and evalAssignmentExpression (state:State) =
        match state.Element with
        | AssignmentExpression (left, Nil, Nil) | AssignmentExpressionNoIn (left, Nil, Nil) ->
            evalConditionalExpression (state.WithElement left)
        | AssignmentExpression (left, AssignmentOperator (Lexer.Punctuator (Lexer.Str "=")), right) 
        | AssignmentExpressionNoIn (left, AssignmentOperator (Lexer.Punctuator (Lexer.Str "=")), right) ->
            let left = evalLeftHandSideExpression (state.WithElement left)
            let right = evalAssignmentExpression (state.WithElement right)
            invoke (constant simpleAssignment) [|left;right|]
        | AssignmentExpression (left, AssignmentOperator (Lexer.Punctuator (Lexer.Str v)), right) 
        | AssignmentExpressionNoIn (left, AssignmentOperator (Lexer.Punctuator (Lexer.Str v)), right)
        | AssignmentExpression (left, AssignmentOperator (Lexer.DivPunctuator (Lexer.Str v)), right) 
        | AssignmentExpressionNoIn (left, AssignmentOperator (Lexer.DivPunctuator (Lexer.Str v)), right) ->
            let args = [|
                evalLeftHandSideExpression (state.WithElement left);
                evalAssignmentExpression (state.WithElement right);
                (match v with
                | "*=" -> constant (fun (l:dyn) (r:dyn) -> l.Op_Multiplication r)
                | "/=" -> constant (fun (l:dyn) (r:dyn) -> l.Op_Division r)
                | "%=" -> constant (fun (l:dyn) (r:dyn) -> l.Op_Modulus r)
                | "+=" -> constant (fun (l:dyn) (r:dyn) -> l.Op_Addition r)
                | "-=" -> constant (fun (l:dyn) (r:dyn) -> l.Op_Subtraction r)
                | "<<=" -> constant (fun (l:dyn) (r:dyn) -> l.Op_LeftShift r)
                | ">>=" -> constant (fun (l:dyn) (r:dyn) -> l.Op_SignedRightShift r)
                | ">>>=" -> constant (fun (l:dyn) (r:dyn) -> l.Op_UnsignedRightShift r)
                | "&=" -> constant (fun (l:dyn) (r:dyn) -> l.Op_BitwiseAnd r)
                | "^=" -> constant (fun (l:dyn) (r:dyn) -> l.Op_BitwiseXor r)
                | "|=" -> constant (fun (l:dyn) (r:dyn) -> l.Op_BitwiseOr r)
                | _ -> failwith "")
            |]
            invoke (constant compoundAssignment) args 

    and evalConditionalExpression (state:State) =
        match state.Element with
        | ConditionalExpression (left, Nil, Nil) | ConditionalExpressionNoIn (left, Nil, Nil) ->
            evalLogicalORExpression (state.WithElement left)
        | ConditionalExpression (test, ifTrue, ifFalse) | ConditionalExpressionNoIn (test, ifTrue, ifFalse) ->
            let test = evalLogicalORExpression (state.WithElement test)
            let ifTrue = evalAssignmentExpression (state.WithElement ifTrue)
            let ifFalse = evalAssignmentExpression (state.WithElement ifFalse)
            condition test ifTrue ifFalse
    
    and evalLogicalORExpression (state:State) =
        match state.Element with
        | LogicalORExpression (Nil, right) | LogicalORExpressionNoIn (Nil, right) ->
            evalLogicalANDExpression (state.WithElement right)
        | LogicalORExpression (left, right) | LogicalORExpressionNoIn (left, right) ->
            let inst = evalLogicalORExpression (state.WithElement left)
            let args = [| evalLogicalANDExpression (state.WithElement right) |]
            call inst Reflection.IDynamic.op_LogicalOr args

    and evalLogicalANDExpression (state:State) =
        match state.Element with
        | LogicalANDExpression (Nil, right) | LogicalANDExpressionNoIn (Nil, right) ->
            evalBitwiseORExpression (state.WithElement right)
        | LogicalANDExpression (left, right) | LogicalANDExpressionNoIn (left, right) ->
            let inst = evalLogicalANDExpression (state.WithElement left)
            let args = [| evalBitwiseORExpression (state.WithElement right) |]
            call inst Reflection.IDynamic.op_LogicalAnd args

    and evalBitwiseORExpression (state:State) =
        match state.Element with
        | BitwiseORExpression (Nil, right) | BitwiseORExpressionNoIn (Nil, right) ->
            evalBitwiseXORExpression (state.WithElement right)
        | BitwiseORExpression (left, right) | BitwiseORExpressionNoIn (left, right) ->
            let inst = evalBitwiseORExpression (state.WithElement left)
            let args = [| evalBitwiseXORExpression (state.WithElement right) |]
            call inst Reflection.IDynamic.op_BitwiseOr args

    and evalBitwiseXORExpression (state:State) =
        match state.Element with
        | BitwiseXORExpression (Nil, right) | BitwiseXORExpressionNoIn (Nil, right) ->
            evalBitwiseANDExpression (state.WithElement right)
        | BitwiseXORExpression (left, right) | BitwiseXORExpressionNoIn (left, right) ->
            let inst = evalBitwiseXORExpression (state.WithElement left)
            let args = [| evalBitwiseANDExpression (state.WithElement right) |]
            call inst Reflection.IDynamic.op_BitwiseXor args

    and evalBitwiseANDExpression (state:State) =
        match state.Element with
        | BitwiseANDExpression (Nil, right) | BitwiseANDExpressionNoIn (Nil, right) ->
            evalEqualityExpression (state.WithElement right)
        | BitwiseANDExpression (left, right) | BitwiseANDExpressionNoIn (left, right) ->
            let inst = evalBitwiseANDExpression (state.WithElement left)
            let args = [| evalEqualityExpression (state.WithElement right) |]
            call inst Reflection.IDynamic.op_BitwiseAnd args

    and evalEqualityExpression (state:State) =    
        match state.Element with
        | EqualityExpression (Nil, Nil, e) | EqualityExpressionNoIn (Nil, Nil, e) ->
            evalRelationalExpression (state.WithElement e)
        | EqualityExpression (left, InputElement (Lexer.Punctuator (Lexer.Str v)), right) 
        | EqualityExpressionNoIn (left, InputElement (Lexer.Punctuator (Lexer.Str v)), right) ->
            let left = evalEqualityExpression (state.WithElement left)
            let right = evalRelationalExpression (state.WithElement right) 
            let mi = 
                match v with
                | "==" -> Reflection.IDynamic.op_Equals
                | "!=" -> Reflection.IDynamic.op_DoesNotEquals
                | "===" -> Reflection.IDynamic.op_StrictEquals
                | "!==" -> Reflection.IDynamic.op_StrictDoesNotEquals
                | _ -> failwith ""
            call left mi [| right |]

    and evalRelationalExpression (state:State) =    
        match state.Element with
        | RelationalExpression (Nil, Nil, e) | RelationalExpressionNoIn (Nil, Nil, e) ->
            evalShiftExpression (state.WithElement e)
        | RelationalExpression (left, InputElement (Lexer.Punctuator (Lexer.Str v)), right) 
        | RelationalExpressionNoIn (left, InputElement (Lexer.Punctuator (Lexer.Str v)), right) ->
            let left = evalRelationalExpression (state.WithElement left)
            let right = evalShiftExpression (state.WithElement right) 
            let mi = 
                match v with
                | "<" -> Reflection.IDynamic.op_Lessthan
                | ">" -> Reflection.IDynamic.op_Greaterthan
                | "<=" -> Reflection.IDynamic.op_LessthanOrEqual
                | ">=" -> Reflection.IDynamic.op_GreaterthanOrEqual
                | "instanceof" -> Reflection.IDynamic.op_Instanceof
                | "in" -> Reflection.IDynamic.op_In
                | _ -> failwith ""
            call left mi [| right |]

    and evalShiftExpression (state:State) =    
        match state.Element with
        | ShiftExpression (Nil, Nil, e) ->
            evalAdditiveExpression (state.WithElement e)
        | ShiftExpression (left, InputElement (Lexer.Punctuator (Lexer.Str v)), right) ->
            let left = evalShiftExpression (state.WithElement left)
            let right = evalAdditiveExpression (state.WithElement right) 
            let mi = 
                match v with
                | "<<" -> Reflection.IDynamic.op_LeftShift
                | ">>" -> Reflection.IDynamic.op_SignedRightShift
                | ">>>" -> Reflection.IDynamic.op_UnsignedRightShift
            call left mi [| right |]

    and evalAdditiveExpression (state:State) =    
        match state.Element with
        | AdditiveExpression (Nil, Nil, e) ->
            evalMultiplicativeExpression (state.WithElement e)
        | AdditiveExpression (left, InputElement (Lexer.Punctuator (Lexer.Str v)), right) ->
            let left = evalAdditiveExpression (state.WithElement left)
            let right = evalMultiplicativeExpression (state.WithElement right) 
            let mi = 
                match v with
                | "+" -> Reflection.IDynamic.op_Addition
                | "-" -> Reflection.IDynamic.op_Subtraction
            call left mi [| right |]

    and evalMultiplicativeExpression (state:State) =    
        match state.Element with
        | MultiplicativeExpression (Nil, Nil, e) ->
            evalUnaryExpression (state.WithElement e)
        | MultiplicativeExpression (left, InputElement (Lexer.Punctuator (Lexer.Str v)), right)
        | MultiplicativeExpression (left, InputElement (Lexer.DivPunctuator (Lexer.Str v)), right) ->
            let left = evalMultiplicativeExpression (state.WithElement left)
            let right = evalUnaryExpression (state.WithElement right) 
            let mi = 
                match v with
                | "*" -> Reflection.IDynamic.op_Multiplication
                | "/" -> Reflection.IDynamic.op_Division
                | "%" -> Reflection.IDynamic.op_Modulus
            call left mi [| right |]

    and evalUnaryExpression (state:State) =    
        match state.Element with
        | UnaryExpression (Nil, e) ->
            evalPostfixExpression (state.WithElement e)
        | UnaryExpression (InputElement (Lexer.Punctuator (Lexer.Str v)), right) ->
            let right = evalPostfixExpression (state.WithElement right) 
            let mi = 
                match v with
                | "++" -> Reflection.IDynamic.op_PrefixIncrement
                | "--" -> Reflection.IDynamic.op_PrefixDecrement
                | "+" -> Reflection.IDynamic.op_Plus
                | "-" -> Reflection.IDynamic.op_Minus
                | "~" -> Reflection.IDynamic.op_BitwiseNot
                | "!" -> Reflection.IDynamic.op_LogicalNot
            call right mi [| |]

    and evalPostfixExpression (state:State) =    
        match state.Element with
        | PostfixExpression (e, Nil) ->
            evalLeftHandSideExpression (state.WithElement e)
        | PostfixExpression (e, InputElement (Lexer.Punctuator (Lexer.Str v))) ->
            let right = evalLeftHandSideExpression (state.WithElement e) 
            let mi = 
                match v with
                | "++" -> Reflection.IDynamic.op_PostfixIncrement
                | "--" -> Reflection.IDynamic.op_PostfixDecrement
            call right mi [| |]

    and evalMemberExpression (state:State) =    
        match state.Element with
        | MemberExpression (e, Nil) ->
            match e with
            | PrimaryExpression (_) ->
                evalPrimaryExpression (state.WithElement e) 
            | FunctionExpression (_, _, _) ->
                evalFunctionExpression (state.WithElement e) 
        | MemberExpression (e1, e2) ->
            match e1, e2 with
            | MemberExpression (_, _), Expression (_, _) -> constant 1 
            | MemberExpression (_, _), InputElement (e3) -> constant 1  
            | MemberExpression (_, _), Arguments (_) ->
                let left = evalMemberExpression (state.WithElement e1)
                let right = [| evalArguments (state.WithElement e2) |]
                call left Reflection.IDynamic.op_Construct right 

    and evalArguments (state:State) = constant 1

    and evalArgumentList (state:State) = constant 1

    and evalCallExpression (state:State) = constant 1

    and evalNewExpression (state:State) =
        match state.Element with
        | NewExpression e ->
            match e with
            | MemberExpression (_, _) ->
                evalMemberExpression (state.WithElement e)                
            | NewExpression _ -> 
                evalNewExpression (state.WithElement e)

    and evalLeftHandSideExpression (state:State) =
        match state.Element with
        | LeftHandSideExpression e ->
            match e with
            | NewExpression _ -> 
                evalNewExpression (state.WithElement e)
            | CallExpression (_, _) -> 
                evalCallExpression (state.WithElement e)

    and evalPrimaryExpression (state:State) =
        match state.Element with
        | PrimaryExpression e ->
            match e with
            | InputElement e ->
                match e with
                | Lexer.IdentifierName (_, _) ->
                    let context = call environmentParam Reflection.IEnvironment.get_Context [||]
                    call context Reflection.IExecutionContext.get_ThisBinding [||]
                | Lexer.Identifier e ->
                    let identifier = Lexer.IdentifierNameParser.evalIdentifierName e
                    let context = call environmentParam Reflection.IEnvironment.get_Context [||]
                    let lex = call context Reflection.IExecutionContext.get_LexicalEnviroment [||]
                    call lex Reflection.ILexicalEnvironment.getIdentifierReference [| constant identifier; constant false|]
                | Lexer.Literal e ->
                    match e with
                    | Lexer.NullLiteral "null" ->
                        call environmentParam Reflection.IEnvironment.get_Null Array.empty
                    | Lexer.BooleanLiteral "true" ->
                        call environmentParam Reflection.IEnvironment.createBoolean [| constant true |]
                    | Lexer.BooleanLiteral "false" ->
                        call environmentParam Reflection.IEnvironment.createBoolean [| constant false |]
                    | Lexer.NumericLiteral _ ->
                        call environmentParam Reflection.IEnvironment.createNumber [| constant (Lexer.NumericLiteralParser.evalNumericLiteral e) |]
                    | Lexer.StringLiteral _ ->
                        call environmentParam Reflection.IEnvironment.createString [| constant (Lexer.StringLiteralParser.evalStringLiteral e) |]
                    | Lexer.RegularExpressionLiteral (_, _, _, _) ->
                        constant 1
            | ArrayLiteral (_, _) ->
                evalArrayLiteral (state.WithElement e)    
            | ObjectLiteral _ ->
                evalObjectLiteral (state.WithElement e)    
            | Expression (_, _) ->
                evalExpression (state.WithElement e)  

    and evalObjectLiteral (state:State) = constant 1

    and evalPropertyNameAndValueList (state:State) = constant 1

    and evalPropertyAssignment (state:State) = constant 1

    and evalPropertyName (state:State) = constant 1

    and evalPropertySetParameterList (state:State) = constant 1

    and evalArrayLiteral (state:State) =
        match state.Element with
        | ArrayLiteral (Nil, e) ->
            let var = exp.Variable(Reflection.IObject.t, "array")
            let construct = call environmentParam Reflection.IEnvironment.constructArray Array.empty
            let assign = exp.Assign (var, construct) :> exp
            let pad = call environmentParam Reflection.IEnvironment.createNumber [| constant (evalElision (state.WithElement e)) |]
            let args = [| constant "length"; pad; constant false  |]
            let put = call var Reflection.IObject.put args
            exp.Block ([|var|], [|assign; put|]) :> exp
        | ArrayLiteral (e, Nil) ->
            evalElementList (state.WithElement e)
        | ArrayLiteral (e1, e2) ->
            let var = exp.Variable(Reflection.IObject.t, "array")
            let assign = exp.Assign (var, evalElementList (state.WithElement e1)) :> exp
            let pad = call environmentParam Reflection.IEnvironment.createNumber [| constant (evalElision (state.WithElement e2)) |]
            let get = call var Reflection.IObject.get [| constant "length" |] 
            let value = call (call pad Reflection.IDynamic.op_Addition [| get |]) Reflection.IDynamic.convertToUInt32 Array.empty
            let put = call var Reflection.IObject.put [| constant "length"; value; constant false  |]
            exp.Block ([|var|], [|assign; put|]) :> exp

    and evalElision (state:State) =
        match state.Element with
        | Elision (Nil) -> 
            1.0
        | Elision (e) -> 
            evalElision (state.WithElement e) + 1.0

    and evalElementList (state:State) = constant 1

    and evalStatement (state:State) =
        match state.Element with
        | Statement e ->
            match e with
            | ExpressionStatement e ->
                evalExpressionStatement (state.WithElement e) 

    and evalBlock (state:State) =
        match state.Element with
        | Block Nil ->
            exp.Empty() :> exp
        | Block e ->
            evalStatementList (state.WithElement e)    

    and evalStatementList (state:State) = constant 1

    and evalVariableStatement (state:State) = constant 1

    and evalVariableDeclarationList (state:State) = constant 1

    and evalVariableDeclarationListNoIn (state:State) = constant 1

    and evalVariableDeclaration (state:State) = constant 1

    and evalVariableDeclarationNoIn (state:State) = constant 1

    and evalInitialiser (state:State) = constant 1

    and evalInitialiserNoIn (state:State) = constant 1

    and evalEmptyStatement (state:State) = constant 1

    and evalExpressionStatement (state:State) =
        match state.Element with
        | ExpressionStatement e ->
            evalExpression (state.WithElement e) 

    and evalIfStatement (state:State) = constant 1

    and evalIterationStatement (state:State) = constant 1

    and evalContinueStatement (state:State) = constant 1

    and evalBreakStatement (state:State) = constant 1

    and evalReturnStatement (state:State) = constant 1

    and evalWithStatement (state:State) = constant 1

    and evalSwitchStatement (state:State) = constant 1

    and evalCaseBlock (state:State) = constant 1

    and evalCaseClauses (state:State) = constant 1

    and evalCaseClause (state:State) = constant 1

    and evalDefaultClause (state:State) = constant 1

    and evalLabelledStatement (state:State) = constant 1

    and evalThrowStatement (state:State) = constant 1

    and evalTryStatement (state:State) = constant 1

    and evalCatch (state:State) = constant 1

    and evalFinally (state:State) = constant 1

    and evalDebuggerStatement (state:State) = constant 1

    and evalFunctionDeclaration (state:State) = constant 1

    and evalFunctionExpression (state:State) = constant 1

    and evalFormalParameterList (state:State) = constant 1

    and evalFunctionBody (state:State) = constant 1

    and evalSourceElement (state:State) =
        match state.Element with
        | SourceElement e ->
            match e with
            | Statement _ ->
                evalStatement (state.WithElement e) 
            | FunctionDeclaration (_, _, _) ->
                evalFunctionDeclaration (state.WithElement e) 

    and evalSourceElements (state:State) =
        match state.Element with
        | SourceElements (Nil, e) ->
            evalSourceElement (state.WithElement e) 
        | SourceElements (e1, e2) ->
            let e1 = evalSourceElements (state.WithElement e1) 
            let e2 = evalSourceElement (state.WithElement e2) 
            block [|e1;e2|]          

    and evalProgram (state:State) =
        match state.Element with
        | Program e ->
            match e with
            | Nil ->
                exp.Empty() :> exp
            | SourceElements (_, _) ->
                evalSourceElements (state.WithElement e)    

    member this.Compile (input:string) =
        let input = Parser.parse input
        let state = State (input, Map.empty, [], [])
        let body = evalProgram state
        exp.Lambda<Code>(body, [| environmentParam |]).Compile()

    member this.Compile (input:SourceElement) =
        let state = State (input, Map.empty, [], [])
        let body = 
            match input with
            | FunctionDeclaration (_, _, _) ->
                evalFunctionDeclaration state
            | FunctionExpression (_, _, _) ->
                evalFunctionExpression state
        exp.Lambda<Code>(body, [| environmentParam |]).Compile()

