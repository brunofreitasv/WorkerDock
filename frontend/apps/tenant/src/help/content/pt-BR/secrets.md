# Armazene e referencie segredos

## Pré-requisitos

Você precisa de acesso **Gerenciar** para configurar perfis ou cabeçalhos de webhook. Um administrador do tenant deve configurar um provedor confiável de segredos. As configurações do cofre mostram o provedor e opções públicas, sem revelar valores secretos.

## Permissão

Usuários com acesso Gerenciar informam referências lógicas de segredo em perfis SQL e destinos de webhook. O provedor configurado fornece e rotaciona o valor fora desses formulários. Usuários com acesso de Leitura não gerenciam credenciais.

## Etapas

1. Solicite ao administrador do provedor que crie a credencial e informe o nome da referência lógica. Combine sua grafia e o processo de rotação.
2. Em um perfil SQL, escolha **Senha do cofre** e informe o usuário SQL e a referência. Em um destino de webhook, adicione o nome do cabeçalho e informe sua referência. Consulte [perfis SQL](#/help:sql-profiles) e [destinos de webhook](#/help:webhook-sinks).
3. Salve a configuração usando somente a referência. Nunca cole senha, token, chave de API ou outro valor secreto no WorkerDock, em um guia ou relatório de suporte.
4. Confirme que a identidade do serviço do tenant pode resolver a referência. O adaptador de desenvolvimento do cofre fica indisponível sem o mapeamento de ambiente exclusivo de desenvolvimento; esta versão não inclui adaptador de produção.

## Exemplo

O administrador cria uma credencial de webhook e informa `tenant/alerts/ops-token`. No destino, adicione o cabeçalho `Authorization` e use essa string como referência de segredo. O WorkerDock armazena a referência; o provedor resolve o valor quando a entrega é executada.

## Verificação

Confira a grafia da referência salva e peça ao administrador que confirme o acesso do tenant. Faça um teste controlado somente após essa confirmação. O WorkerDock nunca exibe os valores resolvidos; falhas mostram um código seguro.

## Recuperação

Em caso de falha de resolução, confira com o administrador a grafia da referência, configuração do provedor, permissões da identidade de serviço e estado da rotação. Se um valor de credencial foi colado em um formulário, rotacione-o com o provedor e remova-o de logs ou anotações expostos.

## Guias relacionados

Consulte [perfis SQL](#/help:sql-profiles), [destinos de webhook](#/help:webhook-sinks), [política de saída](#/help:outbound-policy) e [configurações do tenant](#/help:settings).
