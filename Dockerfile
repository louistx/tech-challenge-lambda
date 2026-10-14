FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY src/TechChallenge.Lambda/ ./
RUN dotnet restore --locked-mode
RUN dotnet publish -c Release --no-restore -o /publish

FROM public.ecr.aws/lambda/dotnet:10 AS lambda
COPY --from=build /publish ${LAMBDA_TASK_ROOT}/
CMD ["TechChallenge.Lambda::TechChallenge.Lambda.Function::FunctionHandler"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS local
WORKDIR /app
COPY --from=build /publish ./
ENV LOCAL_PREFIX=http://+:8080/
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "TechChallenge.Lambda.dll"]
