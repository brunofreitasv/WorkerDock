## Pré-requisitos

Entre no painel do tenant com acesso **Manage**. Abra [Configurações](#/settings) e escolha **Retention**. Para configurar o armazenamento de arquivos ou investigar uma execução de manutenção com falha, fale com o administrador de implantação; a página do tenant não mostra caminhos do sistema de arquivos.

## Permissão

Usuários Read podem consultar este guia. Somente usuários Manage podem alterar as configurações de retenção. Administradores de implantação configuram o diretório de arquivamento e a identidade do serviço scheduler; os procedimentos de migração e recuperação do banco cabem aos operadores descritos em `docs/how-to/manage-retention-and-archives.md`.

## Etapas

1. Revise os três períodos independentes: **Execution logs**, **Execution history** e **Audit history**. Os valores iniciais são 30, 90 e 365 dias. Cada período aceita um número inteiro de 1 a 3650.
2. Verifique se a implantação está pronta para arquivar antes de excluir. Mantenha **Archive eligible records before deleting them** desativado até que um administrador configure e teste o diretório de arquivamento e as permissões do scheduler.
3. Defina **Archive retention (days)** separadamente. Com o arquivamento ativado, use de 1 a 3650 dias. Desativado, o valor 0 mantém os arquivos indefinidamente; um valor positivo pode expirar arquivos criados anteriormente.
4. Selecione **Save changes**. A página valida os números inteiros e exige uma versão atual das configurações antes de enviar a alteração.

## Exemplo prático

Para um tenant que mantém logs de execução por 30 dias, histórico de execuções por 90 dias e auditoria por 365 dias, mantenha os valores iniciais. Se os operadores verificaram o diretório de arquivos e o acesso do scheduler, ative o arquivamento antes da exclusão e defina 365 dias para a retenção dos arquivos. Se o local de arquivamento ainda não estiver pronto, mantenha o arquivamento desativado; essa configuração sozinha não prepara o armazenamento.

## Verificação

Após salvar, confirme que aparece **Retention settings saved** e que os campos mostram os valores escolhidos. Uma alteração de política não apaga dados imediatamente: o scheduler processa lotes limitados e só remove registros elegíveis segundo as proteções de trabalho ativo e repetição segura. Os operadores devem monitorar separadamente a saúde do job de retenção e a capacidade do armazenamento.

## Recuperação

- Se a página informar que falta a versão das configurações, recarregue-a para ler a versão atual do servidor e confira os valores antes de salvar.
- Se outra pessoa alterou a política, a página informa que a versão está desatualizada (HTTP 412). Recarregue, compare a política atual e reaplique sua alteração; não tente salvar usando uma versão antiga.
- Se um valor for rejeitado, use um número inteiro dentro do limite permitido. Com o arquivamento antes da exclusão ativado, a retenção dos arquivos precisa ser de pelo menos 1 dia.
- Se houver falha no armazenamento, nas permissões, no espaço em disco, na finalização ou na verificação de integridade, o WorkerDock preserva os registros de origem correspondentes. Peça a um operador para corrigir o armazenamento e tentar novamente o mesmo lote lógico. Nunca exclua registros de origem ou arquivos parciais manualmente para forçar um status de sucesso.
- O navegador nunca escolhe o caminho de arquivamento. Se o arquivamento estiver indisponível, peça ao administrador para conferir a configuração protegida da implantação, as permissões da identidade do scheduler, o espaço livre e a saúde do job de retenção.

## Guias relacionados

- [Gerencie as configurações do tenant](#/help:settings)
- [Consulte o histórico de auditoria](#/help:audit)
- [Resolva problemas comuns](#/help:troubleshooting)
- Referência para operadores: `docs/how-to/manage-retention-and-archives.md`
