# Gerencie perfis do SQL Server

## Pré-requisitos

Você precisa de acesso **Gerenciar**, do host SQL Server e dos catálogos que as tarefas poderão usar. Confirme que o serviço SQL do tenant consegue acessar o servidor. Para autenticação por senha do cofre, solicite ao provedor de segredos a referência lógica; o formulário nunca recebe a senha em si.

## Permissão

Usuários com acesso Gerenciar podem criar, revisar, diagnosticar e aposentar perfis. Usuários com acesso de Leitura não podem abrir o gerenciamento. Revisões permanecem disponíveis para tarefas e execuções que já as referenciam.

## Etapas

1. Abra [Configurações](#/settings), escolha **Perfis** e depois **Novo perfil**. Informe nome, servidor e um catálogo permitido por linha. As tarefas só podem selecionar catálogos desta lista.
2. Escolha autenticação **Integrada** ou **Senha do cofre**. Para a senha do cofre, informe o usuário SQL e a referência lógica do segredo. Mantenha **Criptografar conexão** ativado. Ative **Confiar no certificado do servidor** somente se necessário e se o risco for conhecido.
3. Escolha **Salvar perfil**. Editar cria uma nova revisão. A aposentadoria impede a seleção por novas tarefas, mas preserva revisões já referenciadas.
4. Selecione um perfil ativo e catálogo permitido, depois escolha **Executar diagnóstico seguro**. Ele usa uma consulta interna fixa de conectividade; não executa a consulta da tarefa nem um procedimento informado por você. Consulte o estado do comando e o registro da execução.

## Exemplo

Para um banco de relatórios, crie **SQL de relatórios**, use o host SQL aprovado pelo tenant e liste `Reporting` como catálogo permitido. Escolha autenticação Integrada se aprovada; caso contrário, selecione Senha do cofre e informe a referência fornecida pelo administrador. Execute o diagnóstico seguro em `Reporting` antes de escolher o perfil em uma tarefa.

## Verificação

Confirme se o perfil aparece com o servidor e estado ativo esperados. Um diagnóstico bem-sucedido comprova conectividade somente para aquele perfil e catálogo naquele momento. O modo de memória armazena perfis, mas não se conecta ao SQL; persistência indisponível não comprova conectividade.

## Recuperação

Se o salvamento indicar revisão desatualizada, recarregue o perfil atual e revise suas alterações. Se o diagnóstico não iniciar, confira servidor, lista de catálogos, autenticação, referência e serviço SQL. Um perfil aposentado não pode ser selecionado por uma nova tarefa; escolha um perfil ativo.

## Guias relacionados

Consulte [tarefas do SQL Server](#/help:sql-jobs), [armazene e referencie segredos](#/help:secrets) e [configurações do tenant](#/help:settings).
