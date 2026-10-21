## Pré-requisitos

Entre no painel do tenant. Você precisa de acesso **Manage** para salvar configurações do tenant. Abra [Configurações](#/settings) e selecione **Geral**, **Provedor de segredos**, **Política de saída**, **Perfis SQL** ou **Retention**. Suas preferências de exibição **Idioma** e **Fuso horário** ficam no cabeçalho do aplicativo.

## Permissão

Usuários com acesso de leitura podem abrir a Documentação e consultar este guia. As páginas de configurações exigem acesso Manage; administradores de implantação cuidam das credenciais do provedor e do armazenamento de arquivos. Um usuário Read pode ver uma mensagem de permissão se abrir diretamente uma rota de configurações.

## Etapas

1. Em **Geral**, defina o nome de exibição do tenant e o fuso horário padrão de exibição e salve. Esse fuso é o padrão do tenant; cada pessoa pode escolher seu próprio fuso no cabeçalho.
2. Em **Provedor de segredos**, confira o provedor instalado e as opções públicas. A página nunca mostra credenciais resolvidas. A implementação do provedor, os mapeamentos de credenciais e a identidade do serviço são configurados por administradores de implantação.
3. Em **Política de saída**, selecione **tenant-jobs** ou **tenant-alerts**. Revise hosts permitidos, portas, CIDRs privados e se HTTP é permitido. Inclua apenas os destinos necessários ao fluxo correspondente. Endereços de loopback, metadados e link-local continuam bloqueados pelas regras fixas.
4. Em **Perfis SQL**, consulte perfis nomeados e catálogos permitidos. Configure as credenciais do perfil com referências lógicas de segredo; use o diagnóstico para verificar a conectividade quando o adaptador instalado oferecer suporte. Consulte [perfis do SQL Server](#/help:sql-profiles).
5. Em **Retention**, defina a retenção de registros e arquivos. O caminho de arquivamento e a identidade do serviço são configurações de implantação, não configurações do navegador. Consulte [retenção e arquivos](#/help:retention) antes de ativar o arquivamento antes da exclusão.
6. Salve na seção alterada. Confira se a mensagem de sucesso apareceu antes de sair da página.

## Exemplo prático

Imagine que uma tarefa chame `reports.example.test` por HTTPS e envie alertas a `notify.example.test`. Em **Política de saída**, selecione **tenant-jobs**, inclua somente o host de relatórios e a porta necessária e salve. Depois selecione **tenant-alerts** e permita somente o host de notificações. Assim, as políticas de execução de tarefas e de entrega de alertas podem ser revisadas separadamente. Configure a referência de segredo de cada tarefa no fluxo de integração correspondente; não informe valores secretos nas configurações públicas.

## Verificação

Volte a cada seção e confirme que o valor salvo aparece. A seção **Geral** mostra os valores atuais do tenant; as páginas de políticas e perfis carregam a revisão salva pelo servidor. Uma mensagem de sucesso confirma que a API aceitou a configuração, mas não comprova que um serviço externo ou provedor de segredos de produção está acessível.

## Recuperação

- Se a gravação indicar que as configurações mudaram em outro lugar ou que a versão está desatualizada, recarregue a seção, compare os valores atuais, reaplique apenas as alterações desejadas e salve novamente. A API usa controle de versão para evitar substituir silenciosamente a edição de outra pessoa.
- Se uma página mostrar erro de permissão, peça a um administrador do tenant para conceder a função Manage adequada no provedor de identidade. Acesso à Documentação não concede acesso para configurar.
- Se um provedor ou diagnóstico SQL estiver indisponível, confira se o adaptador necessário e as configurações de implantação estão instalados. O modo de desenvolvimento em memória não comprova prontidão de banco, cofre, saída de rede ou arquivos em produção.
- Se um destino for rejeitado, revise a política selecionada e o host e a porta exatos. Destinos privados exigem configuração explícita; endereços bloqueados pelas regras fixas não podem ser liberados pela interface do tenant.

## Guias relacionados

- [Gerencie perfis do SQL Server](#/help:sql-profiles)
- [Configure destinos de saída](#/help:outbound-policy)
- [Gerencie retenção e arquivos](#/help:retention)
- [Consulte o histórico de auditoria](#/help:audit)
