# Gerencie perfis do SQL Server

## Pré-requisitos

É necessária a permissão Gerenciar. Tenha o nome do SQL Server e os nomes dos catálogos que devem ser permitidos. Para autenticação por senha do cofre, configure uma referência lógica de senha no provedor de segredos do tenant.

## Permissão

Gerenciar. Os perfis contêm configurações de conexão e revisões; usuários de Leitura não podem abrir o gerenciamento de perfis.

## Etapas

1. Abra [Configurações](#/settings), selecione a aba **Perfis** e depois **Novo perfil**. Informe o nome do perfil, o servidor e um catálogo permitido por linha.
2. Escolha autenticação **Integrada** ou **Senha do cofre**. Na segunda opção, informe o nome de usuário e uma referência lógica de segredo; nunca cole a senha. Mantenha **Criptografar conexão** ativado e use **Confiar no certificado do servidor** somente se o ambiente exigir.
3. Salve o perfil. Ao editar, o salvamento cria uma nova revisão. A aposentadoria impede que novas tarefas selecionem o perfil, mas preserva as revisões já referenciadas por tarefas ou execuções.
4. Selecione um perfil ativo salvo e um catálogo permitido, depois use **Executar diagnóstico seguro**. O diagnóstico verifica a conectividade por uma consulta interna fixa; ele não executa uma consulta ou procedimento informado por você. Consulte o estado do comando e o histórico da execução para ver o resultado.

O modo de memória permite gerenciar perfis e listas de permissão, mas não se conecta ao SQL; um diagnóstico pode informar que a persistência está indisponível. Isso não representa um teste de conectividade bem-sucedido. Um diagnóstico real exige o serviço configurado com SQL e o banco de destino. As configurações SQL de tarefas estão documentadas no guia de criação de tarefas.

## Resultado

O tenant armazena um perfil versionado com uma lista explícita de catálogos permitidos e uma referência de segredo. O diagnóstico informa somente se a verificação limitada de conectividade foi aceita ou falhou de forma segura.
